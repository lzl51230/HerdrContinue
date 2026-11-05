using Watchdog.Core;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class ProxyLatencyTests
{
    [Theory] [InlineData(800, true)] [InlineData(801, false)]
    public async Task CandidateThresholdUsesOneMeasurementOnly(int delay, bool passes)
    {
        var service = new FakeClash { Healthy = false, HealthyNodes = [JapanFast],
            Delays = new() { [JapanFast] = delay } };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false);
        await proxy.CheckRecoveryAsync();
        Assert.Equal(passes, proxy.PermitsAgent);
        Assert.Equal(1, service.Tested.Count(n => n == JapanFast));
        Assert.All(service.TestedUrls, url => Assert.Equal(ProxyPolicy.TestUrl, url));
        if (passes) Assert.Equal(new[] { JapanFast }, service.Selected);
        else Assert.Empty(service.Selected);
    }

    [Fact] public async Task MaintenanceLatencyDoesNotTriggerRecovery()
    {
        var service = new FakeClash { Probe = (_, _) => Task.FromResult(new ProxyProbe(true, 4500, ConnectionSeconds: 61)) };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false);
        await proxy.CheckNowAsync();
        Assert.True(proxy.PermitsAgent); Assert.Empty(service.Tested); Assert.Empty(service.Selected);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task StopOrPreferenceChangeCancelsAllCandidateMeasurements(bool preferences)
    {
        var service = new FakeClash { Healthy = false, SlowTests = true };
        await using var proxy = new ProxyMonitor(service); proxy.Start(false);
        var check = proxy.CheckRecoveryAsync();
        try
        {
            await Eventually(() => service.ActiveTests == 2);
            if (preferences) await proxy.ApplyPreferencesAsync(new(["HK"]));
            else await proxy.StopAsync();
            await check;
            Assert.Equal(0, service.ActiveTests); Assert.Empty(service.Selected);
        }
        finally { await proxy.StopAsync(); await check; }
    }
}
