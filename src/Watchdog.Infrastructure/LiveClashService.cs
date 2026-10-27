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
    Uri? proxyAddress = null, IReadOnlyList<string>? probeUrls = null) : IClashService
{
    private readonly IClashEndpointDiscovery discovery = discovery ?? new ClashEndpointDiscovery();
    private readonly LocalHttpTransport http = new(connections);
    private readonly Uri proxyAddress = proxyAddress ?? new Uri("http://127.0.0.1:7890");
    private readonly IReadOnlyList<string> probeUrls = probeUrls ?? ProxyPolicy.TestUrls;

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
        return snapshots.Count == 1 ? snapshots[0] : throw new HerdrException(snapshots.Count == 0
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
        var tasks = probeUrls.Select(async url =>
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, round.Token);
                return new ProxyProbe(response.StatusCode == HttpStatusCode.NoContent, (int)System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) { return new ProxyProbe(false); }
        }).ToList();
        var all = tasks.ToArray();
        ProxyProbe result = new(false);
        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks); tasks.Remove(finished);
            var probe = await finished;
            if (!probe.Healthy) continue;
            result = probe; round.Cancel(); break;
        }
        await Task.WhenAll(all);
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
        if (!snapshot.CanSelect || !snapshot.Candidates.Any(c => c.Name == node)) throw new HerdrException("拒绝选择非日本节点或不适用的代理组");
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
