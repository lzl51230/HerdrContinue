using System.Net;
using System.Text;
using System.Text.Json;
using Watchdog.Core;

namespace Watchdog.Infrastructure;

public interface IClashEndpointDiscovery
{
    Task<IReadOnlyList<LocalEndpoint>> DiscoverAsync(CancellationToken cancellationToken);
}

public sealed class ClashEndpointDiscovery(string? socketDirectory = null) : IClashEndpointDiscovery
{
    public async Task<IReadOnlyList<LocalEndpoint>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
            return Directory.GetFiles(@"\\.\pipe\").Where(p => p.Contains(@"\MihomoParty\mihomo-", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).Select(p => new LocalEndpoint(EndpointKind.NamedPipe, p)).ToArray();
        if (!OperatingSystem.IsMacOS()) throw new HerdrException("当前平台尚未配置 Clash 控制接口发现");
        var user = await new ProcessRunner().RunAsync("/usr/bin/id", ["-u"], TimeSpan.FromSeconds(2), cancellationToken);
        if (user.ExitCode != 0 || !uint.TryParse(user.Output.Trim(), out var uid)) throw new HerdrException("无法确认当前用户");
        return Directory.EnumerateFiles(socketDirectory ?? "/tmp", $"mihomo-party-{uid}-*.sock")
            .Order(StringComparer.Ordinal).Select(p => new LocalEndpoint(EndpointKind.UnixSocket, p)).ToArray();
    }
}

public sealed record LocalHttpResponse(int Status, byte[] Body);

public sealed class LocalHttpTransport(ILocalConnectionFactory? connections = null)
{
    private readonly ILocalConnectionFactory connections = connections ?? new LocalConnectionFactory();
    public async Task<LocalHttpResponse> RequestAsync(LocalEndpoint endpoint, HttpMethod method, string resource,
        string? body = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3),
            ConnectCallback = (_, ct) => connections.OpenAsync(endpoint, ct)
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(method, new Uri("http://localhost" + resource))
        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream(); var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (output.Length + count > SocketRpc.MaximumResponseBytes) throw new HerdrException("Clash 响应过大");
            output.Write(buffer, 0, count);
        }
        return new((int)response.StatusCode, output.ToArray());
    }
}

public sealed class LiveClashService(IClashEndpointDiscovery? discovery = null, ILocalConnectionFactory? connections = null,
    Uri? proxyAddress = null, IReadOnlyList<string>? probeUrls = null, TimeProvider? timeProvider = null, ClashProfileRepository? profiles = null, string? runtimeStateDirectory = null) : IClashService
{
    private readonly IClashEndpointDiscovery discovery = discovery ?? new ClashEndpointDiscovery();
    private readonly LocalHttpTransport http = new(connections);
    private readonly Uri proxyAddress = proxyAddress ?? new Uri("http://127.0.0.1:7890");
    private readonly IReadOnlyList<string> probeUrls = probeUrls ?? ProxyPolicy.TestUrls;
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    private readonly ClashProfileRepository profiles = profiles ?? new();
    private readonly SemaphoreSlim runtimeGate = new(1, 1);
    private string? loadedKey;
    public async Task PrepareSourcesAsync(ProxyRegionPreferences preferences, CancellationToken ct, Action? changing = null)
    {
        await runtimeGate.WaitAsync(ct);
        try
        {
            var snapshot = await SnapshotAsync(ct);
            var catalogue = profiles.ReadCatalogue(snapshot);
            if (catalogue is null) return;
            var selected = preferences.SelectedSourceIds ?? new[] { catalogue.CurrentId };


            var key = catalogue.Revision + string.Join("|",selected) + snapshot.Endpoint;
            var expected = catalogue.Sources.Where(s=>s.Id != catalogue.CurrentId && selected.Contains(s.Id)).SelectMany(s=>s.Nodes).ToArray();
            if (loadedKey == key && expected.All(n=>snapshot.Nodes.ContainsKey(n.Name))) return;
            if (expected.Length == 0 && !snapshot.Nodes.Keys.Any(n=>n.StartsWith("HDAC-", StringComparison.Ordinal))) { loadedKey=key; return; }
            if (!snapshot.CanSelect) return;
            if (snapshot.Selected?.StartsWith("HDAC-", StringComparison.Ordinal) == true &&
                !catalogue.Sources.SelectMany(s => s.Nodes).Any(n => n.Name == snapshot.Selected))
                throw new IOException("当前跨源节点的缓存不可用，保留当前配置；请恢复原配置后重新应用");
            var payload = ClashProfileRepository.Compose(catalogue,selected,snapshot.Selected);
            var previous = ClashProfileRepository.Compose(catalogue,catalogue.Sources.Where(s=>s.Nodes.Any(n=>snapshot.Nodes.ContainsKey(n.Name))).Select(s=>s.Id),snapshot.Selected);
            var recheck=await SnapshotAsync(ct);
            if(!snapshot.HasSameSelectionContext(recheck)) throw new IOException("配置或节点已变化，请重新应用");
            try
            {
                SaveBaseline(catalogue,snapshot.Selected);
                changing?.Invoke();
                await ReloadAsync(snapshot,payload,ct);
                var after = await SnapshotAsync(ct);
                if (!expected.All(n=>after.Nodes.ContainsKey(n.Name))) throw new IOException("聚合配置未确认加载");
                if (snapshot.Selected is { } node && after.Nodes.ContainsKey(node) && after.Selected != node)
                    await SelectRawAsync(node,after,ct);
                loadedKey=key;
            }
            catch
            {
                var actual=await SnapshotAsync(CancellationToken.None);
                if(actual.Endpoint==snapshot.Endpoint && actual.CurrentSourceId==snapshot.CurrentSourceId && actual.ConfigurationRevision==snapshot.ConfigurationRevision) {
                    await ReloadAsync(snapshot,previous,CancellationToken.None);
                    if (snapshot.Selected is { } node) await SelectRawAsync(node,snapshot,CancellationToken.None);
                }
                throw;
            }
        }
        finally { runtimeGate.Release(); }
    }
    private string BaselinePath(ClashProfileCatalogue catalogue) => Path.Combine(runtimeStateDirectory ?? Path.Combine(Path.GetDirectoryName(AppPaths.AppSettingsPath)!, "ClashRuntime"),ClashProfileRepository.Hash(catalogue.Directory)[..16]+".json");
    private void SaveBaseline(ClashProfileCatalogue catalogue,string? selected)
    {
        var path=BaselinePath(catalogue); var hash=ClashProfileRepository.Hash(catalogue.BaseYaml);
        if(File.Exists(path)) { using var old=JsonDocument.Parse(File.ReadAllText(path)); if(old.RootElement.GetProperty("hash").GetString()==hash) return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(path)!,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(new { hash, payload=catalogue.BaseYaml, selected=selected?.StartsWith("HDAC-",StringComparison.Ordinal)==true ? null : selected }));
        if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        File.Move(temp,path,true);
    }
    private async Task ReloadAsync(ClashSnapshot snapshot,string payload,CancellationToken ct)
    {
        // The client cache may predate a manual mode/port change; preserve the live context.
        var config = ClashProfileRepository.Parse(payload);
        config.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("mode")] = new YamlDotNet.RepresentationModel.YamlScalarNode(snapshot.Mode);
        config.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("mixed-port")] = new YamlDotNet.RepresentationModel.YamlScalarNode(snapshot.MixedPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        payload = ClashProfileRepository.Emit(config);
        var r=await http.RequestAsync(snapshot.Endpoint,HttpMethod.Put,"/configs",JsonSerializer.Serialize(new { payload }),timeout:TimeSpan.FromSeconds(20),cancellationToken:ct);
        if (r.Status != 204) throw new IOException("Clash 配置加载失败，已保留原订阅文件");
    }
    private async Task SelectRawAsync(string name,ClashSnapshot snapshot,CancellationToken ct)
    {
        var r=await http.RequestAsync(snapshot.Endpoint,HttpMethod.Put,"/proxies/GLOBAL",JsonSerializer.Serialize(new {name}),cancellationToken:ct);
        if(r.Status!=204) throw new IOException("无法恢复原节点选择");
    }
    public async Task RestoreSourcesAsync(CancellationToken ct)
    {
        await runtimeGate.WaitAsync(ct);
        try { var s=await SnapshotAsync(ct); var c=profiles.ReadCatalogue(s) ?? throw new IOException("未找到当前订阅配置");
            if(!s.CanSelect) throw new IOException("请先使用全局模式");
            await ReloadAsync(s,c.BaseYaml,ct);
            if(File.Exists(BaselinePath(c))) {
                using var saved=JsonDocument.Parse(File.ReadAllText(BaselinePath(c)));
                if(saved.RootElement.GetProperty("hash").GetString()==ClashProfileRepository.Hash(c.BaseYaml) && saved.RootElement.GetProperty("selected").GetString() is { } selected) {
                    var after=await SnapshotAsync(ct);if(after.Nodes.ContainsKey(selected)) await SelectRawAsync(selected,after,ct);
                }
            }
            loadedKey=null; }
        finally { runtimeGate.Release(); }
    }
    public async Task<ClashSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        List<ClashSnapshot> snapshots = [];
        foreach (var endpoint in await discovery.DiscoverAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var version = await GetAsync(endpoint, "/version", cancellationToken);
                if (!version.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.True) continue;
                var config = await GetAsync(endpoint, "/configs", cancellationToken);
                if (config.GetProperty("mixed-port").GetInt32() != 7890) continue;
                var proxies = await GetAsync(endpoint, "/proxies", cancellationToken);
                var nodes = proxies.GetProperty("proxies").Deserialize<Dictionary<string, ClashNode>>(WireJson.Options) ?? [];
                snapshots.Add(new(endpoint, config.GetProperty("mode").GetString() ?? "", 7890, nodes));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or HerdrException or OperationCanceledException or InvalidOperationException or KeyNotFoundException) { }
        }
        if (snapshots.Count == 1)
        {
            var snapshot = snapshots[0]; var catalogue = profiles.ReadCatalogue(snapshot);
            if(catalogue is null) return snapshot;
            var mapping = catalogue.Sources.SelectMany(s=>s.Nodes).GroupBy(n=>n.Name).ToDictionary(g=>g.Key,g=>g.First());
            return snapshot with { Sources=catalogue.Sources,CurrentSourceId=catalogue.CurrentId,ConfigurationRevision=catalogue.Revision,
                Nodes=snapshot.Nodes.ToDictionary(p=>p.Key,p=>mapping.TryGetValue(p.Key,out var n) ? p.Value with {SourceId=n.SourceId,SourceName=n.SourceName,OriginalName=n.OriginalName} : p.Value.IsProxyLeaf ? p.Value with { SourceId=p.Key.StartsWith("HDAC-",StringComparison.Ordinal) ? "unresolved" : catalogue.CurrentId, SourceName=catalogue.Sources.FirstOrDefault(s=>s.Id==catalogue.CurrentId)?.Name, OriginalName=p.Key } : p.Value) };
        }
        throw new HerdrException(snapshots.Count == 0
            ? "未连接到管理 7890 端口的 Clash Party，稍后重试" : "发现多个 Clash 控制接口，暂停切换以避免误操作");
    }

    public async Task<ProxyProbe> ProbeProxyAsync(CancellationToken cancellationToken)
    {
        using var round = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxyAddress, false) { BypassList = [] }, UseProxy = true, AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.Zero
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var tasks = probeUrls.Select(url => LatencyPolicy.CheckAsync(async ct =>
        {
            var start = timeProvider.GetTimestamp();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.NoContent) throw new HerdrException($"代理探测失败（HTTP {(int)response.StatusCode}）");
            return timeProvider.GetElapsedTime(start).TotalMilliseconds;
        }, round.Token)).ToList();
        var all = tasks.ToArray();
        ProxyProbe result = new(false, Failure: ProxyProbeFailure.RequestFailed);
        try
        {
            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks); tasks.Remove(finished);
                var probe = await finished;
                if (probe.Healthy) { result = probe; break; }
                if (probe.Failure == ProxyProbeFailure.SlowTimeout) result = probe;
            }
        }
        finally
        {
            await round.CancelAsync();
            try { await Task.WhenAll(all); }
            catch (OperationCanceledException) when (round.IsCancellationRequested) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
    public async Task<int> DelayAsync(string node, LocalEndpoint endpoint, string url, CancellationToken cancellationToken)
    {
        var resource = $"/proxies/{Uri.EscapeDataString(node)}/delay?url={Uri.EscapeDataString(url)}&timeout=5000&expected=204";
        var result = await GetAsync(endpoint, resource, cancellationToken, TimeSpan.FromSeconds(6));
        var delay = result.GetProperty("delay").GetInt32();
        return delay is > 0 and <= 5000 ? delay : throw new HerdrException("节点测速失败");
    }
    public async Task SelectAsync(string node, ClashSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!snapshot.CanSelect || !snapshot.Candidates.Any(c => c.Name == node)) throw new HerdrException("拒绝选择未知地区、非代理叶子节点或不适用的代理组");
        cancellationToken.ThrowIfCancellationRequested();
        var response = await http.RequestAsync(snapshot.Endpoint, HttpMethod.Put, "/proxies/GLOBAL", JsonSerializer.Serialize(new { name = node }), cancellationToken: cancellationToken);
        if (response.Status != 204) throw new HerdrException($"Clash 切换请求失败（HTTP {response.Status}）");
    }
    private async Task<JsonElement> GetAsync(LocalEndpoint endpoint, string resource, CancellationToken ct, TimeSpan? timeout = null)
    {
        var response = await http.RequestAsync(endpoint, HttpMethod.Get, resource, timeout: timeout, cancellationToken: ct);
        if (response.Status != 200) throw new HerdrException($"Clash 控制接口异常（HTTP {response.Status}）");
        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.Clone();
    }
}
