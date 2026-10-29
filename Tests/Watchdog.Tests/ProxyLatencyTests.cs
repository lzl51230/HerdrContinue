using System.Collections.Concurrent;
using Watchdog.Core;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class ProxyLatencyTests
{
    [Fact] public async Task BackupOnlyRetriesRequestFailuresAndExcludesConfirmedSlowNodes()
    {
        var service = new FakeClash { Healthy = false, HealthyNodes = [JapanSlow],
            Delays = new() { [JapanFast] = 900 }, Fallback = new() { [JapanFast] = 1, [JapanSlow] = 60 } };
        var events = new List<string>();
        await using var proxy = new ProxyMonitor(service); proxy.Event += events.Add;
        proxy.Start(false); await proxy.CheckNowAsync();
        Assert.True(proxy.PermitsAgent); Assert.Equal(new[] { JapanSlow }, service.Selected);
        Assert.Equal(2, service.Tested.Count(n => n == JapanFast));
        var backup = service.Tested.Zip(service.TestedUrls).Where(p => p.Second == ProxyPolicy.TestUrls[1]);
        Assert.Equal(JapanSlow, Assert.Single(backup).First);
        Assert.Equal(60, Assert.Single(proxy.Status.Delays).Milliseconds);
        Assert.Contains(events, e => e.Contains(JapanFast) && e.Contains("连续两次") && e.Contains("本轮排除"));
    }

    [Fact] public async Task SlowNodesRetestIndependentlyAndRankUsingTheSecondDelay()
    {
        var calls = new ConcurrentDictionary<string, int>();
        var service = new FakeClash { Healthy = false, HealthyNodes = [JapanFast], Measure = async (node, _, ct) =>
        {
            var call = calls.AddOrUpdate(node, 1, (_, previous) => previous + 1);
            await Task.Delay(2, ct);
            return node == JapanFast ? (call == 1 ? 900 : 30) : 50;
        } };
        var progress = new List<int>();
        await using var proxy = new ProxyMonitor(service);
        proxy.StatusChanged += s => { if (s.Phase == ProxyPhase.Testing) progress.Add(s.Completed); };
        proxy.Start(false); await proxy.CheckNowAsync();
        Assert.Equal(2, calls[JapanFast]); Assert.Equal(1, calls[JapanSlow]);
        Assert.Equal(new[] { JapanFast }, service.Selected); Assert.Equal(30, proxy.Status.Delays[0].Milliseconds);
        Assert.Equal(2, proxy.Status.Completed); Assert.All(progress, n => Assert.InRange(n, 0, 2));
        Assert.All(service.TestedUrls, url => Assert.Equal(ProxyPolicy.TestUrls[0], url));
    }

    [Fact] public async Task AllConfirmedSlowNodesSkipBackupAndBecomeEligibleNextRound()
    {
        var service = new FakeClash { Healthy = false, Delays = new() { [JapanFast] = 801, [JapanSlow] = 950 } };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false);
        await proxy.CheckNowAsync(); Assert.Equal(4, service.Tested.Count);
        Assert.Equal(ProxyPhase.Retrying, proxy.Status.Phase); Assert.Empty(proxy.Status.Delays);
        await proxy.CheckNowAsync(); Assert.Equal(8, service.Tested.Count); Assert.Equal(2, proxy.Status.Round);
        Assert.All(service.TestedUrls, url => Assert.Equal(ProxyPolicy.TestUrls[0], url)); Assert.Empty(service.Selected);
    }

    [Fact] public async Task SlowThenFailedRequestRemainsEligibleForBackup()
    {
        var calls = new ConcurrentDictionary<(string Node, string Url), int>();
        var service = new FakeClash { Healthy = false, HealthyNodes = [JapanFast], Measure = (node, url, _) =>
        {
            var count = calls.AddOrUpdate((node, url), 1, (_, previous) => previous + 1);
            if (node != JapanFast) throw new HerdrException("request failed");
            if (url == ProxyPolicy.TestUrls[1]) return Task.FromResult(40);
            return count == 1 ? Task.FromResult(900) : throw new HerdrException("deadline");
        } };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false); await proxy.CheckNowAsync();
        Assert.True(proxy.PermitsAgent); Assert.Equal(new[] { JapanFast }, service.Selected);
        Assert.Equal(2, calls[(JapanFast, ProxyPolicy.TestUrls[0])]); Assert.Equal(1, calls[(JapanFast, ProxyPolicy.TestUrls[1])]);
    }

    [Fact] public async Task CurrentNodeConfirmedSlowTimeoutStartsRecoveryOnFirstCheck()
    {
        var service = new FakeClash { Healthy = false, ProbeFailure = ProxyProbeFailure.SlowTimeout, HealthyNodes = [JapanFast] };
        var paused = false;
        await using var proxy = new ProxyMonitor(service);
        proxy.StatusChanged += s => { if (s.Phase == ProxyPhase.Suspect) paused = !proxy.PermitsAgent; };
        proxy.Start(false); await proxy.CheckNowAsync();
        Assert.True(paused); Assert.Equal(1, proxy.Status.Round); Assert.Equal(new[] { JapanFast }, service.Selected); Assert.True(proxy.PermitsAgent);
    }

    [Fact] public async Task SlowTimeoutBeforeAndAfterSwitchNeverCountsAsRecovered()
    {
        var service = new FakeClash { Healthy = false, Probe = (node, _) => Task.FromResult(node == JapanSlow
            ? new ProxyProbe(true, 100) : new ProxyProbe(false, Failure: ProxyProbeFailure.SlowTimeout)) };
        var events = new List<string>();
        await using var proxy = new ProxyMonitor(service); proxy.Event += events.Add;
        proxy.Start(false); await proxy.CheckNowAsync();
        Assert.Equal(new[] { JapanFast, JapanSlow }, service.Selected); Assert.True(proxy.PermitsAgent);
        Assert.Contains(events, e => e.Contains(JapanFast) && e.Contains("视为节点超时"));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StopOrPreferenceChangeCancelsEveryRetest(bool changePreferences)
    {
        var calls = new ConcurrentDictionary<string, int>();
        var service = new FakeClash { Healthy = false, Measure = async (node, _, ct) =>
        {
            if (calls.AddOrUpdate(node, 1, (_, previous) => previous + 1) == 1) return 900;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 50;
        } };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false); var checking = proxy.CheckNowAsync();
        try
        {
            await Eventually(() => calls.Values.Count(n => n == 2) == 2);
            if (changePreferences) await proxy.ApplyPreferencesAsync(new(["HK"])).WaitAsync(TimeSpan.FromSeconds(3));
            else await proxy.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await checking; Assert.Equal(0, service.ActiveTests); Assert.Empty(service.Selected); Assert.Empty(proxy.Status.Delays);
        }
        finally { await proxy.StopAsync(); await checking; }
    }
}
