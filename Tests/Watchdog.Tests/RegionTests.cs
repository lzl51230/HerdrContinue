using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class RegionTests
{
    private const string Hk = "香港1", HkSlow = "香港2", Sg = "SG-01", Unknown = "请更新订阅-A1";
    private static ProxyRegionPreferences Prefs(params string[] ids) => new(ids);
    private static FakeClash Service(string selected = JapanSlow) => new()
    {
        State = Clash(selected, names: [JapanFast, JapanSlow, Hk, HkSlow, Sg, OtherNode, Unknown]), Healthy = false,
        Delays = new() { [JapanFast] = 100, [JapanSlow] = 200, [Hk] = 30, [HkSlow] = 80, [Sg] = 1, [OtherNode] = 1 }
    };
    [Theory]
    [InlineData("🇭🇰 Hong Kong 01", "HK")]
    [InlineData("香港1-A1", "HK")]
    [InlineData("中國香港 02", "HK")]
    [InlineData("hk01-vless", "HK")]
    [InlineData("日本2", "JP")]
    [InlineData("Japan-03", "JP")]
    [InlineData("JP01", "JP")]
    [InlineData("台灣1", "TW")]
    [InlineData("美国01", "US")]
    [InlineData("USA01", "US")]
    [InlineData("UK 02", "GB")]
    [InlineData("新加坡01", "SG")]
    [InlineData("🇮🇸 Iceland", "IS")]
    [InlineData("🇺🇸 日本中转", "US")]
    [InlineData("🇯🇵🇯🇵 日本", "JP")]
    public void RecognizesFlagsNamesAndCodes(string name, string id) => Assert.Equal(id, RegionResolver.Resolve(name)?.Id);
    [Theory]
    [InlineData("香港 日本 01")]
    [InlineData("🇯🇵🇺🇸 relay")]
    [InlineData("notjp")]
    [InlineData("jpservice")]
    [InlineData("请更新订阅-日本")]
    [InlineData("剩余流量 SG 1TB")]
    [InlineData("节点01")]
    public void AmbiguousAndMetadataNodesAreUnknown(string name) => Assert.Null(RegionResolver.Resolve(name));
    [Fact] public void CatalogueCountsOnlyRecognizedSelectableLeaves()
    {
        var snapshot = Service().State;
        Assert.Equal(4, snapshot.Regions.Length); Assert.Equal(7, snapshot.Leaves.Length); Assert.Equal(6, snapshot.Candidates.Length);
        Assert.Equal(2, snapshot.Regions.Single(r => r.Region.Id == "HK").NodeCount);
        foreach (var type in new[] { "Direct", "Selector", "URLTest", "Fallback", "LoadBalance", "Reject" }) Assert.Null(new ClashNode("日本1", type).Region);
    }
    [Fact] public void CurrentRegionResolvesGroupSelectionAndDetectsNestedManualChange()
    {
        var original = Service().State; var nodes = original.Nodes.ToDictionary();
        nodes["GLOBAL"] = nodes["GLOBAL"] with { Now = "自动选择" };
        nodes["自动选择"] = nodes["自动选择"] with { Now = JapanFast };
        var grouped = original with { Nodes = nodes }; Assert.Equal("JP", grouped.CurrentRegionId);
        var changed = nodes.ToDictionary(); changed["自动选择"] = changed["自动选择"] with { Now = Hk };
        var other = grouped with { Nodes = changed }; Assert.Equal("HK", other.CurrentRegionId); Assert.False(grouped.HasSameSelectionContext(other));
        changed["自动选择"] = changed["自动选择"] with { Now = "GLOBAL" }; Assert.Null((grouped with { Nodes = changed }).CurrentRegionId);
    }
    [Fact] public void RecoveryOrderUsesCurrentThenTopPriorityWithoutRepeating()
    {
        var preferences = Prefs("HK", "JP", "SG");
        Assert.Equal(new[] { "JP", "HK", "SG" }, preferences.RecoveryOrder("JP"));
        Assert.Equal(new[] { "HK", "JP", "SG" }, preferences.RecoveryOrder("US"));
        Assert.Equal(preferences.PriorityRegionIds, preferences.RecoveryOrder(null));
    }
    [Fact] public async Task CurrentSelectedRegionWinsEvenWhenOtherRegionsAreFaster()
    {
        var service = Service(); service.HealthyNodes = [JapanFast, Hk, Sg];
        await using var proxy = new ProxyMonitor(service, preferences: Prefs("HK", "JP", "SG")); proxy.Start(false);
        await proxy.CheckNowAsync(); await proxy.CheckNowAsync();
        Assert.Equal(new[] { JapanFast }, service.Selected); Assert.All(service.Tested, n => Assert.Equal("JP", RegionResolver.Resolve(n)?.Id));
        Assert.True(proxy.PermitsAgent);
    }
    [Fact] public async Task FailedCurrentRegionFallsBackFromTopAndNeverRepeatsIt()
    {
        var service = Service(); service.Delays.Remove(JapanFast); service.Delays.Remove(JapanSlow); service.HealthyNodes = [Sg];
        await using var proxy = new ProxyMonitor(service, preferences: Prefs("HK", "JP", "SG")); proxy.Start(false);
        await proxy.CheckNowAsync(); await proxy.CheckNowAsync();
        Assert.Equal(new[] { Hk, HkSlow, Sg }, service.Selected);
        Assert.All(service.Tested.Take(4), n => Assert.Equal("JP", RegionResolver.Resolve(n)?.Id));
        Assert.DoesNotContain(service.Tested.Skip(4), n => RegionResolver.Resolve(n)?.Id == "JP"); Assert.True(proxy.PermitsAgent);
    }
    [Fact] public async Task UnselectedCurrentRegionAndUnselectedFastNodesAreNeverTested()
    {
        var service = Service(OtherNode); service.HealthyNodes = [Hk, Sg];
        await using var proxy = new ProxyMonitor(service, preferences: Prefs("HK", "SG")); proxy.Start(false);
        await proxy.CheckNowAsync(); await proxy.CheckNowAsync();
        Assert.Equal(new[] { Hk }, service.Selected); Assert.All(service.Tested, n => Assert.Equal("HK", RegionResolver.Resolve(n)?.Id));
    }
    [Fact] public async Task SameRegionTriesNextFastestBeforeMovingToAnotherRegion()
    {
        var service = Service(); service.HealthyNodes = [JapanSlow, Hk];
        // Use a failed current node outside the allowed regions to start at Japan.
        service.State = service.State with { Nodes = Clash(OtherNode, names: service.State.Leaves.Select(n => n.Name).ToArray()).Nodes };
        await using var proxy = new ProxyMonitor(service, preferences: Prefs("JP", "HK")); proxy.Start(false);
        await proxy.CheckNowAsync(); await proxy.CheckNowAsync();
        Assert.Equal(new[] { JapanFast, JapanSlow }, service.Selected); Assert.DoesNotContain(Hk, service.Tested);
    }
    [Fact] public async Task AllFailedRegionsAreRetriedInAFreshRound()
    {
        var service = Service(); service.Delays = []; var time = new ManualTime();
        await using var proxy = new ProxyMonitor(service, new(time), preferences: Prefs("HK", "JP", "SG")); proxy.Start(false);
        await proxy.CheckNowAsync(); await proxy.CheckNowAsync(); var tested = service.Tested.Count;
        Assert.Equal(ProxyPhase.Retrying, proxy.Status.Phase); Assert.Equal(10, proxy.Status.NextCheck);
        time.Now = 10; await proxy.CheckNowAsync(); Assert.Equal(tested * 2, service.Tested.Count); Assert.Equal(2, proxy.Status.Round);
    }
    [Fact] public async Task EmptySelectionAndMissingRegionsDoNotTestOtherRegions()
    {
        foreach (var preferences in new[] { Prefs(), Prefs("IS") })
        {
            var service = Service(); await using var proxy = new ProxyMonitor(service, preferences: preferences); proxy.Start(false);
            await proxy.CheckNowAsync(); await proxy.CheckNowAsync(); Assert.Empty(service.Tested); Assert.Empty(service.Selected); Assert.False(proxy.PermitsAgent);
        }
    }
    [Fact] public async Task ReadOnlyRefreshWhileStoppedNeverProbesOrSelects()
    {
        var service = Service(); await using var proxy = new ProxyMonitor(service);
        await proxy.RefreshCatalogueAsync(); Assert.False(proxy.Running); Assert.Equal(ProxyPhase.Stopped, proxy.Status.Phase);
        Assert.Equal(4, proxy.Status.Regions.Count); Assert.Equal(1, proxy.Status.UnknownNodeCount); Assert.Empty(service.Selected); Assert.Empty(service.Tested);
    }
    [Fact] public async Task StartingDuringCatalogueRefreshChecksHealthAsSoonAsReadFinishes()
    {
        var service = new BlockingCatalogue(); await using var proxy = new ProxyMonitor(service, interval: TimeSpan.FromMinutes(1));
        var refresh = proxy.RefreshCatalogueAsync(); await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        proxy.Start(); service.Release.SetResult(); await refresh;
        await Eventually(() => proxy.PermitsAgent);
    }
    [Fact] public async Task ApplyingPreferencesCancelsAndDrainsOldMeasurements()
    {
        var service = Service(); service.SlowTests = true;
        await using var proxy = new ProxyMonitor(service); proxy.Start(false); await proxy.CheckNowAsync(); var old = proxy.CheckNowAsync();
        await Eventually(() => Volatile.Read(ref service.ActiveTests) > 0);
        await proxy.ApplyPreferencesAsync(Prefs("HK")); await old; Assert.Equal(0, service.ActiveTests); Assert.Empty(service.Selected);
        var previous = service.Tested.Count; service.SlowTests = false; service.HealthyNodes = [Hk]; await proxy.CheckNowAsync();
        Assert.Equal(new[] { Hk }, service.Selected); Assert.All(service.Tested.Skip(previous), n => Assert.Equal("HK", RegionResolver.Resolve(n)?.Id));
    }
    [Fact] public async Task ApplyingWhileHealthyKeepsCurrentNodeAndHealthGate()
    {
        var service = Service(); service.Healthy = true; await using var proxy = new ProxyMonitor(service); proxy.Start(false); await proxy.CheckNowAsync();
        var epoch = proxy.HealthEpoch; await proxy.ApplyPreferencesAsync(Prefs("HK"));
        Assert.True(proxy.PermitsAgent); Assert.Equal(epoch, proxy.HealthEpoch); Assert.Equal(JapanSlow, proxy.Status.Selected); Assert.Empty(service.Selected);
    }
    [Fact] public async Task ApplyingWakesAutomaticLoopAndDoesNotLeaveItCancelled()
    {
        var service = Service(); await using var proxy = new ProxyMonitor(service, interval: TimeSpan.FromMinutes(1)); proxy.Start();
        await Eventually(() => proxy.Status.Phase == ProxyPhase.Suspect); service.HealthyNodes = [Hk];
        await proxy.ApplyPreferencesAsync(Prefs("HK")); await Eventually(() => proxy.PermitsAgent);
        Assert.Equal(new[] { Hk }, service.Selected);
    }
    [Fact] public void DraftSelectionOrderingAndMissingRegionsDoNotMutateActivePolicy()
    {
        var active = Prefs("JP", "IS"); var draft = new RegionSelectionDraft(active);
        draft.SetSelected("HK", true); draft.Move("HK", -1); draft.Move("HK", -1); draft.SetSelected("JP", false);
        Assert.Equal(new[] { "HK", "IS" }, draft.Selected); Assert.Equal(new[] { "JP", "IS" }, active.PriorityRegionIds);
        var rows = draft.Rows(Service().State.Regions); Assert.Equal("HK", rows[0].Region.Id); Assert.Equal(0, rows[1].NodeCount);
        Assert.Equal(draft.Selected, draft.ToPreferences().PriorityRegionIds);
    }
    [Fact] public async Task PreferencesPersistOrderAndExplicitEmptySelection()
    {
        var root = TempDirectory(); var store = new RegionPreferencesStore(Path.Combine(root, "中文 settings.json"));
        try
        {
            Assert.Equal(new[] { "JP" }, (await store.LoadAsync()).Preferences.PriorityRegionIds);
            await store.SaveAsync(Prefs("hk", "UK", "JP", "HK")); Assert.Equal(new[] { "HK", "GB", "JP" }, (await store.LoadAsync()).Preferences.PriorityRegionIds);
            await store.SaveAsync(Prefs()); Assert.Empty((await store.LoadAsync()).Preferences.PriorityRegionIds); Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task CorruptSettingsFallBackWithoutOverwritingOriginal()
    {
        var root = TempDirectory(); var path = Path.Combine(root, "settings.json");
        try
        {
            foreach (var content in new[] { "invalid-json", "{}", "{\"version\":1,\"priorityRegionIds\":[null]}", "{\"version\":1,\"priorityRegionIds\":[\"INVALID\"]}" })
            {
                await File.WriteAllTextAsync(path, content); var loaded = await new RegionPreferencesStore(path).LoadAsync();
                Assert.NotNull(loaded.Warning); Assert.Equal(new[] { "JP" }, loaded.Preferences.PriorityRegionIds); Assert.Equal(content, await File.ReadAllTextAsync(path));
            }
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task SaveFailureLeavesActivePreferencesAndHealthySelectionUntouched()
    {
        var service = Service(); service.Healthy = true; await using var proxy = new ProxyMonitor(service); proxy.Start(false); await proxy.CheckNowAsync();
        var controller = new RegionSettingsController(new FailedStore(), proxy);
        await Assert.ThrowsAsync<IOException>(() => controller.ApplyAsync(Prefs("HK"), default));
        Assert.Equal(new[] { "JP" }, proxy.Preferences.PriorityRegionIds); Assert.True(proxy.PermitsAgent); Assert.Empty(service.Selected);
    }
    private sealed class FailedStore : IRegionPreferencesStore
    {
        public Task<RegionPreferencesLoad> LoadAsync(CancellationToken ct = default) => Task.FromResult(new RegionPreferencesLoad(ProxyRegionPreferences.Default));
        public Task SaveAsync(ProxyRegionPreferences p, CancellationToken ct = default) => throw new IOException("read-only settings");
    }
    private sealed class BlockingCatalogue : IClashService
    {
        private int reads;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ClashSnapshot> SnapshotAsync(CancellationToken ct)
        { if (Interlocked.Increment(ref reads) == 1) { Started.SetResult(); await Release.Task.WaitAsync(ct); } return Clash(); }
        public Task<ProxyProbe> ProbeProxyAsync(CancellationToken ct) => Task.FromResult(new ProxyProbe(true));
        public Task<int> DelayAsync(string n, LocalEndpoint e, string u, CancellationToken ct) => throw new InvalidOperationException();
        public Task SelectAsync(string n, ClashSnapshot s, CancellationToken ct) => throw new InvalidOperationException();
    }
}
