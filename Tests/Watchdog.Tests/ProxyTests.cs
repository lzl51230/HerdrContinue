using Watchdog.Core;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class ProxyPolicyTests
{
    [Fact] public void JapaneseLeavesOnly()
    {
        foreach (var name in new[] { JapanFast, "Japan 01", "JP-02", "Tokyo JP", "日本节点" }) Assert.True((new ClashNode(name).Region?.Id == "JP"), name);
        foreach (var name in new[] { OtherNode, "SG-01", "notjp", "jpservice", "Tokyo", "剩余流量" }) Assert.False((new ClashNode(name).Region?.Id == "JP"), name);
        foreach (var type in new[] { "Selector", "URLTest", "Fallback", "LoadBalance", "Relay", "DIRECT", "REJECT" }) Assert.False((new ClashNode(JapanFast, type).Region?.Id == "JP"));
        Assert.False((new ClashNode(JapanFast, All: [OtherNode]).Region?.Id == "JP")); Assert.Equal(3, Clash().Candidates.Length);
    }
    [Fact] public void RankingIgnoresOtherRegionsAndInvalidDelays()
    {
        var ranked = ProxyPolicy.Ranked([new(OtherNode, 1), new(JapanSlow, 100), new(JapanFast, 20), new("JP zero", 0), new("JP timeout", 6000), new("JP slow", 801)], new HashSet<string> { JapanFast, JapanSlow, "JP zero", "JP timeout", "JP slow" });
        Assert.Equal(new[] { JapanFast, JapanSlow }, ranked.Select(d => d.Name));
    }
    [Fact] public void FreshHealthExpires()
    {
        var status = new ProxyStatus { Phase = ProxyPhase.Healthy, HealthyAt = 100 };
        Assert.True(status.PermitsAgent(109.999)); Assert.False(status.PermitsAgent(110)); Assert.False(status.PermitsAgent(99));
    }
    [Fact] public void SnapshotDetectsManualChangeAndRestart()
    { Assert.False(Clash().HasSameSelectionContext(Clash(JapanFast))); Assert.False(Clash().HasSameSelectionContext(Clash(endpoint: "/tmp/new.sock"))); Assert.False(Clash(mode: "rule").CanSelect); }
}

public sealed class ProxyMonitorTests
{
    [Fact] public async Task HealthyNonJapaneseNodeIsPreserved()
    {
        var service = new FakeClash(); await using var proxy = new ProxyMonitor(service); proxy.Start(false); await proxy.CheckRecoveryAsync();
        Assert.True(proxy.PermitsAgent); Assert.Equal(OtherNode, proxy.Status.Selected); Assert.Empty(service.Selected); Assert.Empty(service.Tested);
    }
    [Fact] public async Task TwoFailedConnectionsSelectFastestJapaneseNode()
    {
        var service = new FakeClash { Healthy = false, HealthyNodes = [JapanFast] }; await using var proxy = new ProxyMonitor(service); proxy.Start(false);
        await proxy.CheckRecoveryAsync(); Assert.Equal(new[] { JapanFast }, service.Selected); Assert.All(service.Tested, n => Assert.Contains(n, new[] { JapanFast, JapanSlow })); Assert.True(proxy.PermitsAgent);
    }
    [Fact] public async Task VerificationFailureTriesNextJapaneseNode()
    {
        var service = new FakeClash { Healthy = false, HealthyNodes = [JapanSlow] }; await using var proxy = new ProxyMonitor(service); proxy.Start(false);
        await proxy.CheckRecoveryAsync(); Assert.Equal(new[] { JapanFast, JapanSlow }, service.Selected); Assert.True(proxy.PermitsAgent);
    }
    [Fact] public async Task AllTimeoutsRetryWithoutLimitAndWithoutFallbackCountry()
    {
        var service = new FakeClash { Healthy = false, Delays = [] }; var time = new ManualTime();
        await using var proxy = new ProxyMonitor(service, new(time)); proxy.Start(false); await proxy.CheckRecoveryAsync(); time.Now = 10; await proxy.CheckRecoveryAsync();
        Assert.Equal(ProxyPhase.Retrying, proxy.Status.Phase); Assert.Equal(14, proxy.Status.NextCheck);
        for (var i = 0; i < 6; i++) { time.Now = 20 + i * 10; await proxy.CheckRecoveryAsync(); }
        Assert.Empty(service.Selected); Assert.Equal(8, proxy.Status.Round); Assert.Equal(8 * 2, service.Tested.Count); Assert.False(proxy.PermitsAgent);
    }
    [Fact] public async Task NoJapaneseNodesAndRuleModeNeverSelect()
    {
        foreach (var state in new[] { Clash(names: [OtherNode]), Clash(mode: "rule") })
        {
            var service = new FakeClash { Healthy = false, State = state }; await using var proxy = new ProxyMonitor(service); proxy.Start(false);
            await proxy.CheckRecoveryAsync(); Assert.Empty(service.Selected); Assert.False(proxy.PermitsAgent);
        }
    }
    [Fact] public async Task ManualSelectionOrReloadInvalidatesMeasurements()
    {
        foreach (var state in new[] { Clash(JapanSlow), Clash(endpoint: "/tmp/restarted.sock"), Clash(names: [JapanSlow, OtherNode]) })
        {
            var service = new FakeClash { Healthy = false, Mutation = state }; await using var proxy = new ProxyMonitor(service); proxy.Start(false);
            await proxy.CheckRecoveryAsync(); Assert.Empty(service.Selected); Assert.Equal(ProxyPhase.Retrying, proxy.Status.Phase);
        }
    }
    [Fact] public async Task AmbiguousWriteUsesReadbackInsteadOfRepeating()
    {
        var service = new FakeClash { Healthy = false, HealthyNodes = [JapanFast], SelectionError = true }; await using var proxy = new ProxyMonitor(service); proxy.Start(false);
        await proxy.CheckRecoveryAsync(); Assert.Equal(new[] { JapanFast }, service.Selected); Assert.True(proxy.PermitsAgent);
    }
    [Fact] public async Task ControllerUnavailableDoesNotPreventHealthyProxyUse()
    {
        var service = new FakeClash { FailSnapshot = true }; await using var proxy = new ProxyMonitor(service); proxy.Start(false);
        await proxy.CheckRecoveryAsync(); Assert.True(proxy.PermitsAgent); service.Healthy = false; await proxy.CheckRecoveryAsync();
        Assert.Equal(ProxyPhase.Unavailable, proxy.Status.Phase); Assert.False(proxy.PermitsAgent);
        service.FailSnapshot = false; service.Healthy = true; await proxy.CheckRecoveryAsync(); Assert.True(proxy.PermitsAgent);
    }
    [Fact] public async Task StopCancelsSlowTestsAndPreventsSelection()
    {
        var service = new FakeClash { Healthy = false, SlowTests = true }; await using var proxy = new ProxyMonitor(service, interval: TimeSpan.FromMilliseconds(10)); proxy.Start();
        await Eventually(() => Volatile.Read(ref service.ActiveTests) > 0); await proxy.StopAsync();
        Assert.Empty(service.Selected); Assert.Equal(0, service.ActiveTests); Assert.Equal(ProxyPhase.Stopped, proxy.Status.Phase);
    }
    [Fact] public async Task EveryNodeStartsBeforeAnyMeasurementCompletes()
    {
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeClash { Healthy = false, State = Clash(names: Enumerable.Range(1, 11).Select(i => "JP-" + i).ToArray()),
            HealthyNodes = ["JP-1"], Measure = (_, _, ct) => release.Task.WaitAsync(ct) };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false); var checking = proxy.CheckRecoveryAsync();
        try
        {
            await Eventually(() => Volatile.Read(ref service.ActiveTests) == 11);
            Assert.Equal(11, service.MaximumConcurrent); Assert.Equal(0, proxy.Status.Completed);
        }
        finally { release.TrySetResult(50); await checking; }
        Assert.Equal(new[] { "JP-1" }, service.Selected); Assert.Equal(11, proxy.Status.Completed);
    }
}
