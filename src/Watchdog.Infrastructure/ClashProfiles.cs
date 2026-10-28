using System.Security.Cryptography;
using System.Text;
using Watchdog.Core;
using YamlDotNet.RepresentationModel;

namespace Watchdog.Infrastructure;

public sealed class ClashProfileException(string message) : IOException(message);

public sealed record ClashProfileCatalogue(string Directory, string CurrentId, string Revision, string BaseYaml,
    IReadOnlyList<ProxySource> Sources, IReadOnlyDictionary<string, YamlMappingNode[]> Definitions);

// Reads only client caches. Subscription downloads and scripts remain owned by Clash Party.
public sealed class ClashProfileRepository(string? directory = null)
{
    internal static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream(); stream.Load(new StringReader(yaml));
        return stream.Documents.Single().RootNode as YamlMappingNode ?? throw new ClashProfileException("配置不是 YAML 对象");
    }
    internal static string Emit(YamlMappingNode node)
    { var stream = new YamlStream(new YamlDocument(node)); using var writer = new StringWriter(); stream.Save(writer, false); return writer.ToString(); }
    internal static YamlNode? Get(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    internal static string? Text(YamlMappingNode node, string key) => (Get(node, key) as YamlScalarNode)?.Value;
    internal static IEnumerable<YamlMappingNode> Items(YamlMappingNode node, string key) =>
        (Get(node, key) as YamlSequenceNode)?.Children.OfType<YamlMappingNode>() ?? [];
    internal static string Read(string path)
    { if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new ClashProfileException("订阅配置过大"); return File.ReadAllText(path); }
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string RuntimeName(string id, string name) => "HDAC-" + Hash(id)[..16] + " | " + name;
    private static IEnumerable<string> Directories()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mihomo-party");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "mihomo-party");
        foreach(var name in new[] { "Clash Party", "clash-party", "mihomo-party" })
        foreach(var process in System.Diagnostics.Process.GetProcessesByName(name))
        {
            string? executable=null;
            try { executable=process.MainModule?.FileName; } catch(System.ComponentModel.Win32Exception) {} catch(InvalidOperationException) {}
            finally { process.Dispose(); }
            if(executable is not null && Path.GetDirectoryName(executable) is { } dir && File.Exists(Path.Combine(dir,"PORTABLE")))
                yield return Path.Combine(dir,"data");
        }
    }
    public ClashProfileCatalogue? ReadCatalogue(ClashSnapshot snapshot)
    {
        var roots = directory is not null ? new[] { directory } : Directories().Distinct().ToArray();
        var found = new List<ClashProfileCatalogue>();
        foreach (var root in roots.Where(r => File.Exists(Path.Combine(r, "profile.yaml"))))
        {
            var indexText = Read(Path.Combine(root, "profile.yaml")); var index = Parse(indexText);
            var current = Text(index, "current"); if (string.IsNullOrEmpty(current)) continue;
            var currentId = Hash(Path.GetFullPath(root))[..16] + ":" + current;
            var app = Parse(Read(Path.Combine(root, "config.yaml")));
            var work = Path.Combine(root, "work", Text(app, "diffWorkDir") == "true" ? current : "");
            var baseYaml = Read(Path.Combine(work, "config.yaml")); var basis = Parse(baseYaml);
            var baseNodes = Items(basis, "proxies").ToArray();
            if (baseNodes.Any(n => !snapshot.Nodes.ContainsKey(Text(n,"name") ?? ""))) continue;
            List<ProxySource> sources = []; Dictionary<string, YamlMappingNode[]> definitions = [];
            var revision = new StringBuilder(indexText).Append(baseYaml);
            foreach (var item in Items(index, "items"))
            {
                var id = Text(item,"id"); if (string.IsNullOrEmpty(id) || Path.GetFileName(id) != id) continue;
                var key = Hash(Path.GetFullPath(root))[..16] + ":" + id; var name = Text(item,"name") ?? id;
                try
                {
                    var text = id == current ? baseYaml : Read(Path.Combine(root,"profiles",id+".yaml")); revision.Append(text);
                    var sourceWork = Path.Combine(root,"work",Text(app,"diffWorkDir")=="true" ? id : "");
                    var profile = Parse(text); var nodes = Items(profile,"proxies").ToList();
                    if (Get(profile,"proxy-providers") is YamlMappingNode providers)
                    foreach (var provider in providers.Children.Values.OfType<YamlMappingNode>())
                    {
                        if (Get(provider,"override") is not null || Get(provider,"filter") is not null || Get(provider,"exclude-filter") is not null)
                            throw new ClashProfileException("此缓存包含 provider 覆写或过滤，请先将其转换为内联节点");
                        if (Get(provider,"payload") is YamlSequenceNode payload) nodes.AddRange(payload.Children.OfType<YamlMappingNode>());
                        else
                        {
                            var path = Text(provider,"path") ?? throw new ClashProfileException("provider 尚无本地缓存路径");
                            var cached = Read(Path.GetFullPath(path, sourceWork)); revision.Append(cached);
                            nodes.AddRange(Items(Parse(cached),"proxies"));
                        }
                    }
                    var names = nodes.Select(n => Text(n,"name") ?? throw new ClashProfileException("节点缺少名称")).ToHashSet(StringComparer.Ordinal);
                    if (names.Count != nodes.Count) throw new ClashProfileException("同一订阅内存在重复节点名");
                    foreach (var node in nodes)
                    {
                        var dependency = Text(node,"dialer-proxy");
                        if (dependency is not null && dependency != "DIRECT" && !names.Contains(dependency)) throw new ClashProfileException("节点依赖未缓存，无法聚合");
                        foreach (var field in new[] { "ca", "certificate", "private-key" })
                            if (Text(node,field) is { } value && !value.Contains('\n'))
                            { var resolved = Path.GetFullPath(value, sourceWork); if (File.Exists(resolved)) node.Children[new YamlScalarNode(field)] = new YamlScalarNode(resolved); }
                    }
                    definitions[key] = nodes.ToArray();
                    sources.Add(new(key,name,nodes.Select(n => new ClashNode(id == current ? Text(n,"name")! : RuntimeName(key,Text(n,"name")!),
                        Text(n,"type") ?? "unknown", SourceId:key, SourceName:name, OriginalName:Text(n,"name"))).Where(n=>n.IsProxyLeaf).ToArray()));
                }
                catch (Exception e) when (e is IOException or YamlDotNet.Core.YamlException or ArgumentException)
                { sources.Add(new(key,name,[],e is ClashProfileException ? e.Message : "本地缓存缺失或格式无效")); }
            }
            found.Add(new(root,currentId,Hash(revision.ToString()),baseYaml,sources,definitions));
        }
        return found.Count switch { 0 => null, 1 => found[0], _ => throw new ClashProfileException("多个订阅目录与当前内核匹配") };
    }
    public static string Compose(ClashProfileCatalogue catalogue, IEnumerable<string> selectedIds, string? pinned = null)
    {
        var basis = Parse(catalogue.BaseYaml);
        var nodes = Get(basis,"proxies") as YamlSequenceNode ?? new YamlSequenceNode(); basis.Children[new YamlScalarNode("proxies")] = nodes;
        foreach (var source in catalogue.Sources.Where(s => s.Id != catalogue.CurrentId && (selectedIds.Contains(s.Id) || s.Nodes.Any(n=>n.Name==pinned))))
        {
            if (!catalogue.Definitions.TryGetValue(source.Id,out var definitions)) continue;
            foreach (var original in definitions)
            {
                var node = Parse(Emit(original)); var name = Text(node,"name")!;
                node.Children[new YamlScalarNode("name")] = new YamlScalarNode(RuntimeName(source.Id,name));
                if (Text(node,"dialer-proxy") is { } dependency && dependency != "DIRECT")
                    node.Children[new YamlScalarNode("dialer-proxy")] = new YamlScalarNode(RuntimeName(source.Id,dependency));
                nodes.Add(node);
            }
        }
        return Emit(basis);
    }
}
