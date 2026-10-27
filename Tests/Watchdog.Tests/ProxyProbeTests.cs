using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;

namespace Watchdog.Tests;

public sealed class ProxyProbeTests
{
    [Theory]
    [InlineData(800, 900, true, 1, 800)]
    [InlineData(801, 800, true, 2, 800)]
    [InlineData(900, 950, false, 2, null)]
    public async Task ActualProxyRequestsApplyTheSharedLatencyRule(int first, int second, bool healthy, int requests, int? latency)
    {
        var time = new ManualTime(); var elapsed = 0; var count = 0;
        await using var proxy = new ProbeServer((_, _) =>
        {
            time.Now = (elapsed += ++count == 1 ? first : second) / 1000d;
            return Task.FromResult(204);
        });
        var service = new LiveClashService(proxyAddress: proxy.Address, probeUrls: ["http://watchdog.invalid/test"], timeProvider: time);
        var result = await service.ProbeProxyAsync(default);
        Assert.Equal(healthy, result.Healthy); Assert.Equal(latency, result.LatencyMs); Assert.Equal(requests, proxy.Requests.Count);
        Assert.Equal(healthy ? ProxyProbeFailure.None : ProxyProbeFailure.SlowTimeout, result.Failure);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnexpectedHttpResponseIsARequestFailure(bool slowFirst)
    {
        var time = new ManualTime(); var count = 0;
        await using var proxy = new ProbeServer((_, _) =>
        {
            time.Now = ++count * 0.9;
            return Task.FromResult(slowFirst && count == 1 ? 204 : 503);
        });
        var service = new LiveClashService(proxyAddress: proxy.Address, probeUrls: ["http://watchdog.invalid/test"], timeProvider: time);
        var result = await service.ProbeProxyAsync(default);
        Assert.False(result.Healthy); Assert.Equal(ProxyProbeFailure.RequestFailed, result.Failure);
        Assert.Equal(slowFirst ? 2 : 1, proxy.Requests.Count);
    }

    [Fact] public async Task TwoAddressesRequireTwoSlowSamplesEach()
    {
        await using var proxy = new ProbeServer((_, _) => Task.FromResult(204));
        string[] urls = ["http://watchdog.invalid/first", "http://watchdog.invalid/second"];
        var service = new LiveClashService(proxyAddress: proxy.Address, probeUrls: urls, timeProvider: new AdvancingTime());
        var result = await service.ProbeProxyAsync(default);
        Assert.False(result.Healthy); Assert.Equal(ProxyProbeFailure.SlowTimeout, result.Failure);
        Assert.All(urls, url => Assert.Equal(2, proxy.Requests.Count(r => r == url)));
    }

    [Fact] public async Task PassingAddressCancelsAnotherAddressDuringRetest()
    {
        var time = new ManualTime(); var slowCalls = 0; var fastCalls = 0;
        var retestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxy = new ProbeServer(async (url, ct) =>
        {
            if (url.EndsWith("/slow", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref slowCalls) == 1) { time.Now = 0.9; return 204; }
                retestStarted.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 204;
            }
            await retestStarted.Task.WaitAsync(ct);
            time.Now = 0.9 + Interlocked.Increment(ref fastCalls) * 0.1; return 204;
        });
        var service = new LiveClashService(proxyAddress: proxy.Address,
            probeUrls: ["http://watchdog.invalid/slow", "http://watchdog.invalid/fast"], timeProvider: time);
        var result = await service.ProbeProxyAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.Healthy); Assert.InRange(result.LatencyMs!.Value, 1, 800);
        Assert.Equal(2, slowCalls); Assert.InRange(fastCalls, 1, 2);
    }

    [Fact] public async Task CallerCancellationDrainsBothAddressChecks()
    {
        await using var proxy = new ProbeServer(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 204; });
        var service = new LiveClashService(proxyAddress: proxy.Address,
            probeUrls: ["http://watchdog.invalid/first", "http://watchdog.invalid/second"]);
        using var cancellation = new CancellationTokenSource(); var checking = service.ProbeProxyAsync(cancellation.Token);
        try { await TestData.Eventually(() => proxy.Requests.Count == 2); }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class AdvancingTime : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Add(ref ticks, 900);
    }

    private sealed class ProbeServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentBag<Task> handlers = [];
        private readonly Task acceptLoop;
        public Uri Address { get; }
        public ConcurrentQueue<string> Requests { get; } = [];
        public ProbeServer(Func<string, CancellationToken, Task<int>> respond)
        {
            listener.Start(); Address = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            acceptLoop = AcceptAsync();
            async Task AcceptAsync()
            {
                try { while (!lifetime.IsCancellationRequested) handlers.Add(HandleAsync(await listener.AcceptTcpClientAsync(lifetime.Token))); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
            async Task HandleAsync(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        using var stream = client.GetStream();
                        var url = (await LocalFixture.ReadLineAsync(stream, lifetime.Token)).Split(' ')[1];
                        while ((await LocalFixture.ReadLineAsync(stream, lifetime.Token)).Length > 0) { }
                        Requests.Enqueue(url); var status = await respond(url, lifetime.Token);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), lifetime.Token);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                    catch (IOException) { }
                }
            }
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync(); await acceptLoop; await Task.WhenAll(handlers);
            listener.Stop(); lifetime.Dispose();
        }
    }
}
