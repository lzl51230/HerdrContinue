using Watchdog.Core;
using Xunit;
using static Watchdog.Tests.TestData;
namespace Watchdog.Tests;
public sealed class MonitorTests
{
    [Fact] public async Task FirstFailureSubmitsImmediatelyAndDoesNotDuplicate()
    {
        var service = new FakeHerdr(); var time = new ManualTime(); await using var m = new MonitorEngine(service,new FakeClash(),timeProvider:time);
        await m.StartAsync(false); await m.StartAsync(false); await m.Proxy.CheckNowAsync(); await m.ScanNowAsync(); await Eventually(()=>service.Sent==1);
        await Eventually(()=>m.Snapshot.Events.Any(e=>e.Message.Contains("Herdr 已确认提交")));
        time.Now=10; await m.ScanNowAsync(); await m.StopAsync(); Assert.Equal(1,service.Sent);
    }
    [Fact] public async Task ProxyRecoveryDoesNotAddAnInitialCooldown()
    {
        var service=new FakeHerdr(); var clash=new FakeClash {Healthy=false,Delays=[]}; var time=new ManualTime();
        await using var m=new MonitorEngine(service,clash,timeProvider:time); await m.StartAsync(false); await m.Proxy.CheckNowAsync(); await m.ScanNowAsync(); Assert.Equal(0,service.Sent);
        time.Now=100; clash.Healthy=true; await m.Proxy.CheckNowAsync(); await m.ScanNowAsync(); await Eventually(()=>service.Sent==1);
    }
    [Fact] public async Task ProxyOutageDoesNotResetRemainingAgentCooldown()
    {
        var service=new FakeHerdr();var clash=new FakeClash {Delays=[]};var time=new ManualTime();await using var m=new MonitorEngine(service,clash,timeProvider:time);
        await m.StartAsync(false);await m.Proxy.CheckNowAsync();await m.ScanNowAsync();await Eventually(()=>m.Snapshot.Events.Any(e=>e.Message.Contains("Herdr 已确认提交")));
        time.Now=1;service.Value=Observe(sequence:3);clash.Healthy=false;await m.Proxy.CheckNowAsync();await m.ScanNowAsync();Assert.Equal(1,service.Sent);
        time.Now=3;clash.Healthy=true;await m.Proxy.CheckNowAsync();await m.ScanNowAsync();Assert.Equal(5,m.Snapshot.Rows[0].Deadline);
        m.SetCooldownSeconds(10);Assert.Equal(10,m.Snapshot.Rows[0].Deadline);time.Now=9.999;await m.ScanNowAsync();Assert.Equal(1,service.Sent);
        time.Now=10;await m.ScanNowAsync();await Eventually(()=>service.Sent==2);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StopOrProxyFailureCancelsRevalidation(bool outage)
    {
        var service=new FakeHerdr {SlowRefresh=true};var clash=new FakeClash {Delays=[]};await using var m=new MonitorEngine(service,clash);
        await m.StartAsync(false);await m.Proxy.CheckNowAsync();await m.ScanNowAsync();await service.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if(outage){clash.Healthy=false;await m.Proxy.CheckNowAsync();}await m.StopAsync();Assert.Equal(0,service.Sent);Assert.All(m.Snapshot.Rows,r=>Assert.Null(r.Deadline));
    }
    [Fact] public async Task ReplacementCannotReceiveInput()
    {
        var service=new FakeHerdr {Replacement=true};await using var m=new MonitorEngine(service,new FakeClash());await m.StartAsync(false);await m.Proxy.CheckNowAsync();await m.ScanNowAsync();
        await Eventually(()=>m.Snapshot.Events.Any(e=>e.Message.StartsWith("已取消本次发送")));Assert.Equal(0,service.Sent);
    }
    [Fact] public async Task DiscoveryFailureClearsPendingCountdown()
    {
        var service = new FakeHerdr { SlowRefresh = true }; var time = new ManualTime();
        await using var monitor = new MonitorEngine(service, new FakeClash(), timeProvider: time);
        await monitor.StartAsync(false); await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync();
        await service.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        service.Offline = true; time.Now = 20; await monitor.ScanNowAsync();
        Assert.Null(monitor.Snapshot.Rows[0].Deadline); await monitor.StopAsync(); Assert.Equal(0, service.Sent);
    }
    [Fact] public async Task StoppingDuringRevalidationDrainsAndRestartCanSendImmediately()
    {
        var service = new FakeHerdr { SlowRefresh = true };
        await using var monitor = new MonitorEngine(service, new FakeClash());
        await monitor.StartAsync(false); await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync();
        await service.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await monitor.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); service.SlowRefresh = false;
        await monitor.StartAsync(false); await monitor.Proxy.CheckNowAsync(); await monitor.ScanNowAsync();
        await Eventually(() => service.Sent == 1);
    }
}
