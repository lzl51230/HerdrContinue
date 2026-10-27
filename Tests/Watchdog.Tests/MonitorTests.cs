using Watchdog.Core;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class MonitorTests
{
    [Fact] public async Task StopCancelsPendingSendAndRepeatedStartDoesNotDuplicate()
    {
        var service = new FakeHerdr(); var time = new ManualTime(); await using var monitor = new MonitorEngine(service, new FakeClash(), timeProvider: time);
        await monitor.StartAsync(false); await monitor.StartAsync(false); await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync();
        Assert.Single(monitor.Snapshot.Rows); Assert.NotNull(monitor.Snapshot.Rows[0].Deadline); await monitor.StopAsync(); time.Now = 50; await monitor.ScanNowAsync();
        Assert.Equal(0, service.Sent); Assert.False(monitor.Snapshot.Running);
    }
    [Fact] public async Task MonitorSubmitsOnceThenWaitsForNewEvidence()
    {
        var service = new FakeHerdr(); var time = new ManualTime(); await using var monitor = new MonitorEngine(service, new FakeClash(), timeProvider: time);
        await monitor.StartAsync(false); await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync(); time.Now = 10; await monitor.ScanNowAsync();
        await Eventually(() => monitor.Snapshot.Events.Any(e => e.Message.Contains("Herdr 已确认提交")));
        time.Now = 100; await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync(); await monitor.StopAsync(); Assert.Equal(1, service.Sent);
    }
    [Fact] public async Task FreshIdentityCheckPreventsSendingToReplacement()
    {
        var service = new FakeHerdr { Replacement = true }; var time = new ManualTime(); await using var monitor = new MonitorEngine(service, new FakeClash(), timeProvider: time);
        await monitor.StartAsync(false); await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync(); time.Now = 10; await monitor.ScanNowAsync();
        await Eventually(() => monitor.Snapshot.Events.Any(e => e.Message.StartsWith("已取消本次发送"))); Assert.Equal(0, service.Sent);
    }
    [Fact] public async Task DiscoveryFailureClearsPendingCountdown()
    {
        var service = new FakeHerdr(); var time = new ManualTime(); await using var monitor = new MonitorEngine(service, new FakeClash(), timeProvider: time);
        await monitor.StartAsync(false); await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync(); service.Offline = true; time.Now = 20; await monitor.ScanNowAsync();
        Assert.Null(monitor.Snapshot.Rows[0].Deadline); Assert.Equal(0, service.Sent);
    }
    [Fact] public async Task ProxyOutageBlocksAndRecoveryStartsFullCooldown()
    {
        var service = new FakeHerdr(); var clash = new FakeClash { Healthy = false, Delays = [] }; var time = new ManualTime();
        await using var monitor = new MonitorEngine(service, clash, timeProvider: time); await monitor.StartAsync(false);
        await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync(); time.Now = 100; await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync();
        Assert.Equal(0, service.Sent); Assert.Null(monitor.Snapshot.Rows[0].Deadline);
        clash.Healthy = true; await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync(); Assert.Equal(110, monitor.Snapshot.Rows[0].Deadline);
        time.Now = 109.99; await monitor.ScanNowAsync(); Assert.Equal(0, service.Sent); time.Now = 110; await monitor.ScanNowAsync();
        await Eventually(() => service.Sent == 1); await monitor.StopAsync(); Assert.Equal(ProxyPhase.Stopped, monitor.Snapshot.Proxy.Phase);
    }
    [Fact] public async Task ProxyFailureDuringAgentRevalidationCancelsPendingInput()
    {
        var service = new FakeHerdr { SlowRefresh = true }; var clash = new FakeClash { Delays = [] }; var time = new ManualTime();
        await using var monitor = new MonitorEngine(service, clash, timeProvider: time); await monitor.StartAsync(false);
        await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync(); time.Now = 10; await monitor.ScanNowAsync(); await service.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clash.Healthy = false; await monitor.Proxy.CheckNowAsync(); await monitor.StopAsync(); Assert.Equal(0, service.Sent); Assert.Null(monitor.Snapshot.Rows[0].Deadline);
    }
    [Fact] public async Task ConfirmedSlowTimeoutPausesAgentAndRecoveryRestartsFullCooldown()
    {
        var service = new FakeHerdr(); var clash = new FakeClash { Delays = [] }; var time = new ManualTime();
        await using var monitor = new MonitorEngine(service, clash, timeProvider: time); await monitor.StartAsync(false);
        await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync(); Assert.Equal(10, monitor.Snapshot.Rows[0].Deadline);
        time.Now = 5; clash.Healthy = false; clash.ProbeFailure = ProxyProbeFailure.SlowTimeout;
        await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync();
        Assert.Equal(1, monitor.Proxy.Status.Round); Assert.Null(monitor.Snapshot.Rows[0].Deadline); Assert.Equal(0, service.Sent);
        time.Now = 100; clash.Healthy = true; await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync();
        Assert.Equal(110, monitor.Snapshot.Rows[0].Deadline); time.Now = 109.99; await monitor.ScanNowAsync(); Assert.Equal(0, service.Sent);
        time.Now = 110; await monitor.ScanNowAsync(); await Eventually(() => service.Sent == 1);
    }
    [Fact] public async Task CloseWhileRevalidatingDrainsTasksAndCanRestart()
    {
        var service = new FakeHerdr { SlowRefresh = true }; var time = new ManualTime(); await using var monitor = new MonitorEngine(service, new FakeClash(), timeProvider: time);
        await monitor.StartAsync(false); await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync(); time.Now = 10; await monitor.ScanNowAsync(); await service.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await monitor.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); service.SlowRefresh = false; await monitor.StartAsync(false); await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync();
        Assert.Equal(20, monitor.Snapshot.Rows[0].Deadline); Assert.Equal(0, service.Sent);
    }
}
