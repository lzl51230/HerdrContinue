using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using Watchdog.Core;

namespace Watchdog.Infrastructure;

/// <summary>A credential-free heartbeat session. Replacing its TCP tunnel is a failure, not a successful retry.</summary>
public sealed class OpenAiConnectionProbe(Uri? proxyAddress = null, Uri? target = null, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly Uri proxyAddress = proxyAddress ?? new("http://127.0.0.1:7890");
    private readonly Uri target = target ?? new(ProxyPolicy.TestUrl);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private HttpClient? client;
    private long connections;
    private long? established, lastHeartbeat;
    internal SslClientAuthenticationOptions TlsOptions { get; } = new();

    public async Task<ProxyProbe> HeartbeatAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var remaining = lastHeartbeat is { } last
                ? ProxyPolicy.SilenceTimeout - time.GetElapsedTime(last) : ProxyPolicy.SilenceTimeout;
            if (remaining <= TimeSpan.Zero) return Failed("连续 10 秒未收到有效心跳");
            using var timeout = new CancellationTokenSource(remaining, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                client ??= CreateClient();
                var start = time.GetTimestamp();
                using var request = new HttpRequestMessage(HttpMethod.Get, target)
                { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.UserAgent.ParseAdd("HerdrAutoContinue-Probe/1.0");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                    return Failed($"Codex 心跳返回 HTTP {(int)response.StatusCode}（预期 401）");
                // Consume bounded bodies so the same TLS tunnel can actually return to the pool.
                await using var body = await response.Content.ReadAsStreamAsync(linked.Token);
                var buffer = new byte[4096]; var total = 0; int count;
                while ((count = await body.ReadAsync(buffer, linked.Token)) > 0)
                    if ((total += count) > 65536) return Failed("Codex 心跳响应过大");
                if (response.Headers.ConnectionClose == true) return Failed("服务端关闭了长连接");
                if (Interlocked.Read(ref connections) != 1) return Failed("底层 TCP/TLS 连接已重建");
                linked.Token.ThrowIfCancellationRequested();
                var now = time.GetTimestamp();
                established ??= now; lastHeartbeat = now;
                return new(true, Math.Max(1, (int)Math.Ceiling(time.GetElapsedTime(start, now).TotalMilliseconds)),
                    ConnectionSeconds: time.GetElapsedTime(established.Value, now).TotalSeconds,
                    HeartbeatAt: now / (double)time.TimestampFrequency);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { Close(); throw; }
            catch (OperationCanceledException) { return Failed("建链或心跳连续 10 秒无有效响应"); }
            catch (Exception e) when (e is HttpRequestException or IOException or SocketException)
            { return Failed("TCP/TLS 连接失败：" + e.Message); }
        }
        finally { gate.Release(); }
    }

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxyAddress, false) { BypassList = [], Credentials = null },
            UseProxy = true, UseCookies = false, Credentials = null, AllowAutoRedirect = false,
            MaxConnectionsPerServer = 1, MaxResponseHeadersLength = 32,
            SslOptions = TlsOptions,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan, PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            ConnectTimeout = Timeout.InfiniteTimeSpan,
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, ct);
                    Interlocked.Increment(ref connections);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private ProxyProbe Failed(string reason)
    {
        var seconds = established is { } first && lastHeartbeat is { } last ? time.GetElapsedTime(first, last).TotalSeconds : 0;
        Close();
        return new(false, Failure: ProxyProbeFailure.RequestFailed, ConnectionSeconds: seconds, Detail: reason);
    }
    private void Close()
    { client?.Dispose(); client = null; established = lastHeartbeat = null; Interlocked.Exchange(ref connections, 0); }
    public async Task ResetAsync(CancellationToken ct = default)
    { await gate.WaitAsync(ct); try { Close(); } finally { gate.Release(); } }
    public async ValueTask DisposeAsync() => await ResetAsync();
}
