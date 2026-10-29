using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;
using static Watchdog.Tests.TestData;
namespace Watchdog.Tests;
public sealed class FeatureToggleTests
{
    [Theory] [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task FourCombinationsRunIndependently(bool proxy, bool agent)
    {
        var service=new FakeHerdr();var clash=new FakeClash {Healthy=false,HealthyNodes=[JapanFast]};
        await using var m=new MonitorEngine(service,clash);
        await m.ApplyAppSettingsAsync(new(ProxyRecoveryEnabled:proxy,AgentRecoveryEnabled:agent));
        Assert.False(m.Snapshot.Running);Assert.False(m.Proxy.Running);
        await m.StartAsync(false);await m.Proxy.CheckNowAsync();await m.ScanNowAsync();
        if(agent) await Eventually(()=>service.Sent==1);
        else {Assert.Single(m.Snapshot.Rows);Assert.Contains("自动续接已关闭",m.Snapshot.Rows[0].Detail);Assert.Null(m.Snapshot.Rows[0].Deadline);Assert.Equal(0,service.Sent);}
        Assert.Equal(proxy,m.Proxy.Running);Assert.Equal(proxy,clash.Selected.Count==1);Assert.Equal(proxy,m.Snapshot.ProxyRecoveryEnabled);
    }
    [Fact] public async Task DisableCancelsRevalidationThenReenableUsesFreshObservation()
    {
        var service=new FakeHerdr {SlowRefresh=true};await using var m=new MonitorEngine(service,new FakeClash());
        await m.StartAsync(false);await m.Proxy.CheckNowAsync();await m.ScanNowAsync();await service.RefreshStarted.Task;
        await m.ApplyAppSettingsAsync(new(AgentRecoveryEnabled:false)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(m.Proxy.Running);Assert.Equal(0,service.Sent);Assert.Null(m.Snapshot.Rows[0].Deadline);
        service.SlowRefresh=false;await m.ApplyAppSettingsAsync(new());await m.ScanNowAsync();await Eventually(()=>service.Sent==1);
        await Eventually(()=>m.Snapshot.Events.Any(e=>e.Message.Contains("Herdr 已确认提交")));
        await m.ApplyAppSettingsAsync(new(AgentRecoveryEnabled:false));await m.ApplyAppSettingsAsync(new());await m.ScanNowAsync();Assert.Equal(1,service.Sent);
    }
    [Fact] public async Task DisablingProxyDrainsMeasurementsAndReleasesAgentHealthGate()
    {
        var clash=new FakeClash {Healthy=false,SlowTests=true};var service=new FakeHerdr();await using var m=new MonitorEngine(service,clash);
        await m.StartAsync(false);var check=m.Proxy.CheckNowAsync();await Eventually(()=>clash.ActiveTests>0);
        await m.ScanNowAsync();Assert.Equal(0,service.Sent);
        await m.ApplyAppSettingsAsync(new(ProxyRecoveryEnabled:false)).WaitAsync(TimeSpan.FromSeconds(3));await check;
        Assert.Equal(0,clash.ActiveTests);Assert.Empty(clash.Selected);await m.ScanNowAsync();await Eventually(()=>service.Sent==1);
    }
    [Theory] [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task FlagsRoundTrip(bool proxy,bool agent)
    {
        var root=TempDirectory();try{var store=new AppSettingsStore(Path.Combine(root,"app.json"));var value=new AppSettings(true,AppTheme.Monokai,true,10,proxy,agent);await store.SaveAsync(value);Assert.Equal(value,(await store.LoadAsync()).Settings);}finally{Directory.Delete(root,true);}
    }
    [Theory] [InlineData("")] [InlineData(",\"proxyRecoveryEnabled\":null")] [InlineData(",\"proxyRecoveryEnabled\":\"false\"")]
    public async Task LegacyAndInvalidFieldsDefaultIndividually(string field)
    {
        var root=TempDirectory();try{var path=Path.Combine(root,"app.json");var json="{\"version\":1,\"autoStartEnabled\":false,\"agentRecoveryEnabled\":false"+field+"}";await File.WriteAllTextAsync(path,json);var loaded=await new AppSettingsStore(path).LoadAsync();Assert.True(loaded.Settings.ProxyRecoveryEnabled);Assert.False(loaded.Settings.AgentRecoveryEnabled);Assert.Equal(field.Length>0,loaded.Warning is not null);Assert.Equal(json,await File.ReadAllTextAsync(path));}finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task TogglePreservesRemainingCooldown()
    {
        var time=new ManualTime();var service=new FakeHerdr();await using var m=new MonitorEngine(service,new FakeClash(),timeProvider:time);
        await m.ApplyAppSettingsAsync(new(ProxyRecoveryEnabled:false));await m.StartAsync(false);await m.ScanNowAsync();
        await Eventually(()=>m.Snapshot.Events.Any(e=>e.Message.Contains("Herdr 已确认提交")));
        await m.ApplyAppSettingsAsync(new(ProxyRecoveryEnabled:false,AgentRecoveryEnabled:false));
        time.Now=3;service.Value=Observe(sequence:3);await m.ScanNowAsync();Assert.Equal(1,service.Sent);
        await m.ApplyAppSettingsAsync(new(ProxyRecoveryEnabled:false));await m.ScanNowAsync();Assert.Equal(5,m.Snapshot.Rows[0].Deadline);
        time.Now=5;await m.ScanNowAsync();await Eventually(()=>service.Sent==2);
    }
    [Fact] public async Task AlreadyIssuedSubmissionCanFinishWithoutResendingAfterToggle()
    {
        var service=new BlockingSubmission();await using var m=new MonitorEngine(service,new FakeClash());
        await m.ApplyAppSettingsAsync(new(ProxyRecoveryEnabled:false));await m.StartAsync(false);await m.ScanNowAsync();await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await m.ApplyAppSettingsAsync(new(ProxyRecoveryEnabled:false,AgentRecoveryEnabled:false)).WaitAsync(TimeSpan.FromSeconds(3));
        service.Release.TrySetResult();await Eventually(()=>m.Snapshot.Events.Any(e=>e.Message.Contains("Herdr 已确认提交")));
        await m.ApplyAppSettingsAsync(new(ProxyRecoveryEnabled:false));await m.ScanNowAsync();Assert.Equal(1,service.Sent);
    }
    [Fact] public async Task DisabledProxyCannotBeRestartedByAnotherLifecycleCaller()
    {
        var service=new FakeClash();await using var proxy=new ProxyMonitor(service);
        proxy.Start(false);await proxy.SetRecoveryEnabledAsync(false);proxy.Start(false);
        Assert.False(proxy.Running);await proxy.CheckNowAsync();Assert.Empty(service.Tested);
        await proxy.SetRecoveryEnabledAsync(true);Assert.False(proxy.Running);proxy.Start(false);await proxy.CheckNowAsync();Assert.True(proxy.PermitsAgent);
    }
    [Fact] public async Task ProxyRecoveryContinuesWithoutHerdr()
    {
        var clash=new FakeClash {Healthy=false,HealthyNodes=[JapanFast]};await using var m=new MonitorEngine(new FakeHerdr {Offline=true},clash);
        await m.ApplyAppSettingsAsync(new(AgentRecoveryEnabled:false));await m.StartAsync(false);await m.ScanNowAsync();await m.Proxy.CheckNowAsync();
        Assert.True(m.Proxy.PermitsAgent);Assert.Equal(new[]{JapanFast},clash.Selected);
    }
    [Fact] public async Task DisabledProxyInitializationOnlyReadsLocalPreferences()
    {
        var root=TempDirectory();try
        {
            var clash=new NoClash();await using var proxy=new ProxyMonitor(clash);
            var controller=new RegionSettingsController(new RegionPreferencesStore(Path.Combine(root,"regions.json")),proxy);
            await controller.InitializeAsync(default,refreshCatalogue:false);Assert.Equal(0,clash.Calls);
        }finally{Directory.Delete(root,true);}
    }
    private sealed class NoClash : IClashService
    {
        public int Calls;
        public Task<ClashSnapshot> SnapshotAsync(CancellationToken ct){Calls++;throw new IOException("No Clash");}
        public Task<ProxyProbe> ProbeProxyAsync(CancellationToken ct){Calls++;throw new IOException("No Clash");}
        public Task<int> DelayAsync(string n,LocalEndpoint e,string u,CancellationToken ct){Calls++;throw new IOException("No Clash");}
        public Task SelectAsync(string n,ClashSnapshot s,CancellationToken ct){Calls++;throw new IOException("No Clash");}
    }
    private sealed class BlockingSubmission : IHerdrService
    {
        private readonly FakeHerdr inner=new();
        public readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Sent;
        public Task<IReadOnlyList<Session>> SessionsAsync(CancellationToken ct)=>inner.SessionsAsync(ct);
        public Task<IReadOnlyList<Agent>> AgentsAsync(Session s,CancellationToken ct)=>inner.AgentsAsync(s,ct);
        public Task<Observation> ObserveAsync(Agent a,Session s,bool refresh,CancellationToken ct)=>inner.ObserveAsync(a,s,refresh,ct);
        public async Task ResumeAsync(Observation o,CancellationToken ct){Sent++;Started.TrySetResult();await Release.Task.WaitAsync(ct);}
    }
}
