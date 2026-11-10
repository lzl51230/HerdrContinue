using Watchdog.Core;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class DirectRecoveryTests
{
    [Theory]
    [InlineData("startup")]
    [InlineData("manual")]
    [InlineData("subscription")]
    public async Task DirectStartsRecoveryInOneCheckWithoutProbingDirect(string change)
    {
        List<string> probed = [];
        var service = new FakeClash
        {
            Probe = (node, _) =>
            {
                probed.Add(node);
                return Task.FromResult(new ProxyProbe(true, ConnectionSeconds: 60));
            }
        };
        await using var proxy = new ProxyMonitor(service);
        proxy.Start(false);
        if (change != "startup")
        {
            await proxy.CheckNowAsync();
            Assert.True(proxy.PermitsAgent);
        }
        var resets = service.Resets;
        service.State = Clash("DIRECT") with { ConfigurationRevision = change == "subscription" ? "new-subscription" : null };
        var events = new List<string>();
        proxy.Event += events.Add;
        await proxy.CheckNowAsync();
        Assert.Equal(new[] { JapanFast }, service.Selected);
        Assert.DoesNotContain("DIRECT", probed);
        Assert.True(service.Resets > resets);
        Assert.True(proxy.PermitsAgent);
        Assert.Contains("检测到 GLOBAL 选择 DIRECT，开始恢复节点", events);
    }

    [Fact]
    public async Task DirectRecoveryRetainsRoundsAndBackoff()
    {
        var time = new ManualTime();
        var service = new FakeClash { State = Clash("DIRECT"), Delays = [] };
        await using var proxy = new ProxyMonitor(service, new(time));
        proxy.Start(false);
        double now = 0;
        var round = 0;
        foreach (var delay in new[] { 2, 4, 8, 16, 30, 30 })
        {
            time.Now = now;
            await proxy.CheckNowAsync();
            Assert.Equal(++round, proxy.Status.Round);
            Assert.Equal(now + delay, proxy.Status.NextCheck);
            Assert.False(proxy.PermitsAgent);
            now += delay;
        }
        Assert.Empty(service.Selected);
    }

    [Fact]
    public async Task DirectCandidateMustRemainConnectedForTenSeconds()
    {
        var time = new ManualTime();
        double age = 0;
        var service = new FakeClash
        {
            State = Clash("DIRECT"),
            Probe = (_, _) => Task.FromResult(new ProxyProbe(true, ConnectionSeconds: age))
        };
        await using var proxy = new ProxyMonitor(service, new(time));
        proxy.Start(false);
        var check = proxy.CheckNowAsync();
        try
        {
            await Eventually(() => proxy.Status.ValidatingCandidate);
            Assert.False(proxy.PermitsAgent);
            age = 9; time.Now = 2;
            await Eventually(() => proxy.Status.ConnectionSeconds == 9);
            Assert.False(proxy.PermitsAgent);
            age = 10; time.Now = 4;
            await check.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(proxy.PermitsAgent);
        }
        finally { await proxy.StopAsync(); await check; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectHonorsCooldownAndRechecksManualChanges(bool manualChange)
    {
        var time = new ManualTime();
        var service = new FakeClash { State = Clash("DIRECT") };
        await using var proxy = new ProxyMonitor(service, new(time));
        proxy.Start(false);
        await proxy.CheckNowAsync();
        Assert.Single(service.Selected);
        service.State = Clash("DIRECT");
        var check = proxy.CheckNowAsync();
        try
        {
            await Eventually(() => proxy.Status.Phase == ProxyPhase.CoolingDown);
            Assert.False(proxy.PermitsAgent);
            time.Now = 4;
            Assert.Single(service.Selected);
            if (manualChange) service.State = Clash(JapanSlow);
            time.Now = 5;
            await check.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(manualChange ? 1 : 2, service.Selected.Count);
        }
        finally { await proxy.StopAsync(); await check; }
    }

    [Fact]
    public async Task ManualChangeDuringDirectMeasurementDiscardsResults()
    {
        var service = new FakeClash { State = Clash("DIRECT"), Mutation = Clash(JapanSlow) };
        await using var proxy = new ProxyMonitor(service);
        proxy.Start(false);
        await proxy.CheckNowAsync();
        Assert.NotEmpty(service.Tested);
        Assert.Empty(service.Selected);
        Assert.False(proxy.PermitsAgent);
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("direct")]
    [InlineData("regions")]
    [InlineData("sources")]
    [InlineData("candidates")]
    [InlineData("disabled")]
    [InlineData("stopped")]
    public async Task DirectDoesNotOverrideSwitchingRestrictions(string restriction)
    {
        var service = new FakeClash { State = Clash("DIRECT", mode: restriction is "rule" or "direct" ? restriction : "global",
            names: restriction == "candidates" ? [OtherNode] : null) };
        var preferences = restriction switch
        {
            "regions" => new ProxyRegionPreferences([]),
            "sources" => new ProxyRegionPreferences(["JP"], []),
            _ => ProxyRegionPreferences.Default
        };
        await using var proxy = new ProxyMonitor(service, preferences: preferences);
        if (restriction == "disabled") await proxy.SetRecoveryEnabledAsync(false);
        proxy.Start(false);
        if (restriction == "stopped") await proxy.StopAsync();
        await proxy.CheckNowAsync();
        Assert.Empty(service.Tested);
        Assert.Empty(service.Selected);
        if (restriction is not "disabled" and not "stopped") Assert.True(proxy.PermitsAgent);
    }

    [Fact]
    public async Task DirectUsesSavedRegionOrderAndSelectedSources()
    {
        var state = Clash("DIRECT");
        state = state with { CurrentSourceId = "A", Nodes = state.Nodes.ToDictionary(p => p.Key,
            p => p.Value.IsProxyLeaf ? p.Value with { SourceId = p.Key == JapanFast ? "B" : "A" } : p.Value) };
        var service = new FakeClash { State = state, Delays = [] };
        await using var proxy = new ProxyMonitor(service, preferences: new(["US", "JP"], ["A"]));
        proxy.Start(false);
        await proxy.CheckNowAsync();
        Assert.Equal(new[] { OtherNode, JapanSlow }, service.Tested);
        Assert.DoesNotContain(JapanFast, service.Tested);
    }
}
