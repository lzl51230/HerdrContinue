using Watchdog.Core;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class LongConnectionTests
{
    [Fact] public async Task HeartbeatPermissionUsesObservationTimeRatherThanPublicationTime()
    {
        var time = new ManualTime(); time.Now = 9;
        var service = new FakeClash { Probe = (_, _) => Task.FromResult(new ProxyProbe(true, HeartbeatAt: 0)) };
        await using var proxy = new ProxyMonitor(service, new(time)); proxy.Start(false); await proxy.CheckNowAsync();
        Assert.True(proxy.PermitsAgent); time.Now = 10; Assert.False(proxy.PermitsAgent);
    }

    [Fact] public async Task FirstShortFailureOnlyReconnectsAndSecondStartsRecovery()
    {
        var service = new FakeClash { Healthy = false, HealthyNodes = [JapanFast] };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false);
        await proxy.CheckNowAsync();
        Assert.Equal(1, proxy.Status.ConsecutiveFailures); Assert.Empty(service.Tested); Assert.False(proxy.PermitsAgent);
        await proxy.CheckNowAsync();
        Assert.Equal(new[] { JapanFast }, service.Selected); Assert.True(proxy.PermitsAgent);
        Assert.Equal(0, proxy.Status.ConsecutiveFailures);
    }

    [Theory] [InlineData(59.999, 2)] [InlineData(60, 1)]
    public async Task SixtySecondsResetsFailureCount(double duration, int expected)
    {
        var samples = new Queue<ProxyProbe>([new(false), new(true, ConnectionSeconds: duration), new(false)]);
        var service = new FakeClash { State = Clash(mode: "rule"), Probe = (_, _) => Task.FromResult(samples.Dequeue()) };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false);
        await proxy.CheckNowAsync(); await proxy.CheckNowAsync(); await proxy.CheckNowAsync();
        Assert.Equal(expected, proxy.Status.ConsecutiveFailures); Assert.Empty(service.Selected);
    }

    [Fact] public async Task MatureConnectionBreakingDoesNotCountAsShortFailure()
    {
        var service = new FakeClash { Probe = (_, _) => Task.FromResult(new ProxyProbe(false, ConnectionSeconds: 60)) };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false); await proxy.CheckNowAsync();
        Assert.Equal(0, proxy.Status.ConsecutiveFailures); Assert.Empty(service.Tested); Assert.False(proxy.PermitsAgent);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CandidateNeedsSuccessfulHeartbeatAtTenSeconds(bool disconnect)
    {
        var time = new ManualTime(); double age = 0;
        var service = new FakeClash { Healthy = false, Delays = new() { [JapanFast] = 100 }, Probe = (node, _) =>
            Task.FromResult(node == JapanFast ? new ProxyProbe(!disconnect || age < 10, ConnectionSeconds: age) : new ProxyProbe(false)) };
        await using var proxy = new ProxyMonitor(service, new(time)); proxy.Start(false);
        var check = proxy.CheckRecoveryAsync();
        try
        {
            await Eventually(() => proxy.Status.ValidatingCandidate);
            age = 9.999; time.Now = 2;
            await Eventually(() => proxy.Status.ConnectionSeconds == 9.999);
            Assert.False(proxy.PermitsAgent); Assert.False(check.IsCompleted);
            age = 10; time.Now = 4;
            await check.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(!disconnect, proxy.PermitsAgent);
        }
        finally { await proxy.StopAsync(); await check; }
    }

    [Fact] public async Task ChangingContextResetsFailureCountAndClosesOldTunnel()
    {
        var service = new FakeClash { Healthy = false };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false);
        await proxy.CheckNowAsync(); var resets = service.Resets;
        service.State = Clash(JapanSlow);
        await proxy.CheckNowAsync();
        Assert.Equal(1, proxy.Status.ConsecutiveFailures); Assert.Empty(service.Tested);
        Assert.True(service.Resets > resets);
        await proxy.StopAsync(); Assert.False(proxy.PermitsAgent);
    }

    [Fact] public async Task RecoveryBackoffCapsAtThirtySeconds()
    {
        var time = new ManualTime(); var service = new FakeClash { Healthy = false, Delays = [] };
        await using var proxy = new ProxyMonitor(service, new(time)); proxy.Start(false);
        await proxy.CheckRecoveryAsync();
        Assert.Equal(2, proxy.Status.NextCheck);
        foreach (var wait in new[] { 4, 8, 16, 30, 30 })
        {
            var now = proxy.Status.NextCheck!.Value; time.Now = now;
            await proxy.CheckNowAsync(); Assert.Equal(now + wait, proxy.Status.NextCheck);
        }
    }

    [Fact] public async Task MonitoringRecoversWithoutAnySelectedRegions()
    {
        var service = new FakeClash { Healthy = false };
        await using var proxy = new ProxyMonitor(service, preferences: new([])); proxy.Start(false);
        await proxy.CheckRecoveryAsync(); Assert.False(proxy.PermitsAgent);
        service.Healthy = true; await proxy.CheckNowAsync();
        Assert.True(proxy.PermitsAgent); Assert.Empty(service.Selected); Assert.Empty(service.Tested);
    }

    [Fact] public async Task EveryRecoveryEpisodeSkipsItsFailedCurrentNodeInTheFirstRound()
    {
        var time = new ManualTime();
        var service = new FakeClash { Healthy = false, HealthyNodes = [JapanFast] };
        await using var proxy = new ProxyMonitor(service, new(time)); proxy.Start(false);
        await proxy.CheckRecoveryAsync(); var tested = service.Tested.Count;
        time.Now = 10; service.HealthyNodes = [JapanSlow];
        await proxy.CheckRecoveryAsync();
        Assert.Equal(new[] { JapanFast, JapanSlow }, service.Selected);
        Assert.DoesNotContain(JapanFast, service.Tested.Skip(tested));
    }

    [Fact] public async Task ContextChangingDuringHeartbeatDiscardsItsSuccess()
    {
        var service = new FakeClash();
        service.Probe = (_, _) =>
        {
            service.State = Clash(JapanSlow);
            return Task.FromResult(new ProxyProbe(true, ConnectionSeconds: 60));
        };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false); await proxy.CheckNowAsync();
        Assert.False(proxy.PermitsAgent); Assert.Empty(service.Selected); Assert.True(service.Resets > 0);
    }

    [Fact] public async Task CancellingCandidateObservationClosesItsConnection()
    {
        var service = new FakeClash { Healthy = false, Delays = new() { [JapanFast] = 100 },
            Probe = (node, _) => Task.FromResult(new ProxyProbe(node == JapanFast)) };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false); await proxy.CheckNowAsync();
        using var cancellation = new CancellationTokenSource();
        var check = proxy.CheckNowAsync(cancellation.Token);
        await Eventually(() => proxy.Status.ValidatingCandidate);
        var resets = service.Resets; cancellation.Cancel(); await check;
        Assert.True(service.Resets > resets); Assert.False(proxy.PermitsAgent); Assert.False(proxy.Status.ValidatingCandidate);
    }
}
