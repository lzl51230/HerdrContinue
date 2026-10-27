using Watchdog.Core;
using Xunit;

namespace Watchdog.Tests;

public sealed class LatencyPolicyTests
{
    [Theory]
    [InlineData(20, 900, true, 1, 20)]
    [InlineData(800, 900, true, 1, 800)]
    [InlineData(801, 800, true, 2, 800)]
    [InlineData(900, 50, true, 2, 50)]
    [InlineData(801, 801, false, 2, null)]
    [InlineData(900, 950, false, 2, null)]
    public async Task SlowResponsesAreConfirmedAtTheSameAddress(double first, double second, bool healthy, int calls, int? latency)
    {
        var count = 0;
        var probe = await LatencyPolicy.CheckAsync(_ => Task.FromResult(++count == 1 ? first : second), default);
        Assert.Equal(healthy, probe.Healthy); Assert.Equal(calls, count); Assert.Equal(latency, probe.LatencyMs);
        Assert.Equal(healthy ? ProxyProbeFailure.None : ProxyProbeFailure.SlowTimeout, probe.Failure);
    }

    [Fact] public async Task FractionalMillisecondsAreComparedBeforeRounding()
    {
        var calls = 0;
        var result = await LatencyPolicy.CheckAsync(_ => Task.FromResult(++calls == 1 ? 800.01 : 799.5), default);
        Assert.True(result.Healthy); Assert.Equal(2, calls); Assert.Equal(800, result.LatencyMs);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(5001)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public async Task InvalidSamplesAreRequestFailures(double sample)
    {
        var calls = 0;
        var result = await LatencyPolicy.CheckAsync(_ => { calls++; return Task.FromResult(sample); }, default);
        Assert.False(result.Healthy); Assert.Equal(1, calls); Assert.Equal(ProxyProbeFailure.RequestFailed, result.Failure);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailedRequestIsNotConfirmedHighLatency(bool slowFirst)
    {
        var calls = 0;
        var result = await LatencyPolicy.CheckAsync(_ =>
        {
            if (++calls == 1 && slowFirst) return Task.FromResult(900d);
            throw new OperationCanceledException("request deadline");
        }, default);
        Assert.Equal(slowFirst ? 2 : 1, calls); Assert.Equal(ProxyProbeFailure.RequestFailed, result.Failure);
    }

    [Fact] public async Task CancellationDuringRetestPropagatesAndDrains()
    {
        using var cancellation = new CancellationTokenSource();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var active = 0;
        var checking = LatencyPolicy.CheckAsync(async ct =>
        {
            if (++calls == 1) return 900;
            Interlocked.Increment(ref active);
            try { retryStarted.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 50; }
            finally { Interlocked.Decrement(ref active); }
        }, cancellation.Token);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking);
        Assert.Equal(0, active); Assert.Equal(2, calls);
    }
}
