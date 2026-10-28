#if DEBUG
using System.Text.Json;
using Watchdog.Core;

namespace HerdrAutoContinue;

// Isolated transport fixture; never opens or modifies the user's Clash controller.
internal sealed class QaClashService(bool recover, string directory) : IClashService
{
    private static bool SourcesMode => Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO")?.StartsWith("sources",StringComparison.Ordinal)==true;
    private readonly object sync = new();
    private string selected = "验收日本01";
    private readonly List<string> tested = [], selections = [];
    private static readonly string[] Names = ["验收日本01", "验收日本02", "验收香港01", "验收香港02", "验收新加坡01", "验收美国01"];
    public Task<ClashSnapshot> SnapshotAsync(CancellationToken ct)
    {
        lock (sync)
        {
            var names=SourcesMode ? Names.Concat(new[]{"验收源B日本01","验收源C日本01"}).ToArray() : Names;
            var nodes = names.ToDictionary(n => n, n => new ClashNode(n,SourceId:SourcesMode ? n.Contains("源B") ? "B" : n.Contains("源C") ? "C" : "A" : null,
                SourceName:SourcesMode ? n.Contains("源B") ? "订阅 B" : n.Contains("源C") ? "订阅 C" : "订阅 A" : null,OriginalName:n));
            nodes["GLOBAL"] = new("GLOBAL", "Selector", selected, names);
            var snapshot=new ClashSnapshot(new(EndpointKind.NamedPipe, "watchdog-qa-clash"), "global", 7890, nodes);
            if(SourcesMode) snapshot=snapshot with {CurrentSourceId="A",Sources=nodes.Values.Where(n=>n.IsProxyLeaf).GroupBy(n=>n.SourceId!).Select(g=>new ProxySource(g.Key,g.First().SourceName!,g.ToArray())).ToArray()};
            return Task.FromResult(snapshot);
        }
    }
    public Task<ProxyProbe> ProbeProxyAsync(CancellationToken ct)
    { lock (sync) return Task.FromResult(new ProxyProbe(SourcesMode ? selected=="验收源B日本01" : !recover || selected == "验收香港01", 15)); }
    public async Task<int> DelayAsync(string node, LocalEndpoint endpoint, string url, CancellationToken ct)
    {
        if (!recover && !SourcesMode) throw new InvalidOperationException("Healthy QA proxy never tests nodes");
        await Task.Delay(20, ct);
        lock (sync)
        {
            tested.Add(node); Trace();
            if(SourcesMode) return node.Contains("源B")?20:node.Contains("源C")?1:100;
            return RegionResolver.Resolve(node)?.Id switch { "JP" => throw new HerdrException("QA timeout"), "HK" => node.EndsWith("01") ? 30 : 80, _ => 10 };
        }
    }
    public Task SelectAsync(string node, ClashSnapshot snapshot, CancellationToken ct)
    {
        if (!recover && !SourcesMode) throw new InvalidOperationException("Healthy QA proxy never switches nodes");
        ct.ThrowIfCancellationRequested();
        lock (sync) { selected = node; selections.Add(node); Trace(); }
        return Task.CompletedTask;
    }
    private void Trace()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"clash-trace-{Environment.ProcessId}.json"), JsonSerializer.Serialize(new { tested, selected = selections }));
    }
}
#endif
