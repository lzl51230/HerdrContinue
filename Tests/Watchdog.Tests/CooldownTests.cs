using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;
using static Watchdog.Tests.TestData;
namespace Watchdog.Tests;
public sealed class CooldownTests
{
    [Theory] [InlineData("")] [InlineData(",\"cooldownSeconds\":0")] [InlineData(",\"cooldownSeconds\":301")] [InlineData(",\"cooldownSeconds\":1.5")] [InlineData(",\"cooldownSeconds\":null")] [InlineData(",\"cooldownSeconds\":\"10\"")]
    public async Task LegacyAndInvalidValuesPreserveOtherPreferences(string field)
    {
        var root=TempDirectory();try {
            var path=Path.Combine(root,"app-settings.json");var json="{\"version\":1,\"autoStartEnabled\":true,\"theme\":\"monokai\",\"silentStartEnabled\":true"+field+"}";
            await File.WriteAllTextAsync(path,json);var loaded=await new AppSettingsStore(path).LoadAsync();
            Assert.Equal(new AppSettings(true,AppTheme.Monokai,true,5),loaded.Settings);Assert.Equal(field.Length>0,loaded.Warning is not null);Assert.Equal(json,await File.ReadAllTextAsync(path));
        }finally{Directory.Delete(root,true);}
    }
    [Theory] [InlineData(1)] [InlineData(5)] [InlineData(10)] [InlineData(300)]
    public async Task ValidValuesRoundTrip(int seconds)
    { var root=TempDirectory();try{var store=new AppSettingsStore(Path.Combine(root,"settings.json"));await store.SaveAsync(new(CooldownSeconds:seconds));Assert.Equal(seconds,(await store.LoadAsync()).Settings.CooldownSeconds);}finally{Directory.Delete(root,true);} }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EachSwitchWaitsRemainingTimeAndSettingsWakeWaiter(bool extend)
    {
        var time=new ManualTime();var service=new FakeClash {Healthy=false,HealthyNodes=[JapanSlow]};await using var proxy=new ProxyMonitor(service,new(time));proxy.Start(false);
        var check=proxy.CheckNowAsync();try{
            await Eventually(()=>proxy.Status.Phase==ProxyPhase.CoolingDown);Assert.Equal(new[]{JapanFast},service.Selected);Assert.Equal(5,proxy.Status.SwitchDeadline);
            time.Now=3;proxy.SetCooldownSeconds(extend?10:1);
            if(extend){await Eventually(()=>proxy.Status.SwitchDeadline==10);time.Now=9.999;Assert.Single(service.Selected);time.Now=10;}
            await check.WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(new[]{JapanFast,JapanSlow},service.Selected);Assert.True(proxy.PermitsAgent);
        }finally{await proxy.StopAsync();await check;}
    }
    [Theory] [InlineData("stop")] [InlineData("preferences")] [InlineData("manual")] [InlineData("healthy")]
    public async Task WaitingSwitchRevalidatesAndCancels(string action)
    {
        var time=new ManualTime();var service=new FakeClash {Healthy=false,HealthyNodes=[JapanSlow]};await using var proxy=new ProxyMonitor(service,new(time));proxy.Start(false);var check=proxy.CheckNowAsync();
        try{
            await Eventually(()=>proxy.Status.Phase==ProxyPhase.CoolingDown);
            if(action=="stop")await proxy.StopAsync();
            else if(action=="preferences")await proxy.ApplyPreferencesAsync(new(["HK"]));
            else {if(action=="manual")service.State=Clash(OtherNode);else service.Healthy=true;time.Now=5;}
            await check.WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(new[]{JapanFast},service.Selected);
            if(action=="healthy")Assert.True(proxy.PermitsAgent);
        }finally{await proxy.StopAsync();await check;}
    }
    [Fact] public async Task RealTimerSpacesActualNodeSwitches()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var seen = new Dictionary<string, double>();
        var service = new FakeClash { Healthy = false };
        service.Probe = (node, _) =>
        {
            seen.TryAdd(node, watch.Elapsed.TotalSeconds);
            return Task.FromResult(new ProxyProbe(node == JapanSlow, 100));
        };
        await using var proxy = new ProxyMonitor(service);
        proxy.SetCooldownSeconds(1); proxy.Start(false);
        await proxy.CheckNowAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(new[] { JapanFast, JapanSlow }, service.Selected);
        Assert.True(seen[JapanSlow] - seen[JapanFast] >= .98);
    }
}
