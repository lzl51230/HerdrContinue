using Watchdog.Core;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class ProxyLifecycleTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task StopDuringConfigurationCannotBeUndone(bool restore, bool fail)
    {
        var clash = new LifecycleClash();
        await using var engine = new MonitorEngine(new FakeHerdr(), clash);
        await engine.StartAsync(false);
        var save = new SaveBarrier(fail);
        var configure = engine.Proxy.ConfigureAsync(new(["JP"], ["B"]), save.Save, default, restore);
        await save.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await engine.StopAsync();
        save.Release.SetResult();
        await Finish(configure, fail);
        Assert.False(engine.Snapshot.Running);
        Assert.False(engine.Proxy.Running);
        await engine.Proxy.CheckNowAsync();
        Assert.Equal(0, clash.Probes);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ConfigurationPreservesManualLoopMode(bool fail)
    {
        var clash = new LifecycleClash();
        await using var proxy = new ProxyMonitor(clash);
        proxy.Start(false);
        await Finish(proxy.ConfigureAsync(new(["JP"], ["B"]),
            () => fail ? Task.FromException(new IOException("save failed")) : Task.CompletedTask, default), fail);
        Assert.True(proxy.Running);
        // A manual loop has no background task, even when its configuration was changed.
        var field = typeof(ProxyMonitor).GetField("loop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Null(field.GetValue(proxy));
        await proxy.CheckNowAsync();
        Assert.Equal(1, clash.Probes);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LatestStartIntentWaitsForConfiguration(bool restart)
    {
        await using var proxy = new ProxyMonitor(new LifecycleClash());
        proxy.Start(false);
        var save = new SaveBarrier();
        var configure = proxy.ConfigureAsync(new(["JP"], ["B"]), save.Save, default);
        await save.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await proxy.SetRecoveryEnabledAsync(false);
        await proxy.SetRecoveryEnabledAsync(true);
        if (restart) proxy.Start(false);
        Assert.False(proxy.Running);
        save.Release.SetResult();
        await configure;
        Assert.Equal(restart, proxy.Running);
    }

    [Fact]
    public async Task DisposeWaitsForConfigurationAndPreventsRestart()
    {
        var proxy = new ProxyMonitor(new LifecycleClash());
        proxy.Start(false);
        var save = new SaveBarrier();
        var configure = proxy.ConfigureAsync(new(["JP"], ["B"]), save.Save, default);
        await save.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var disposal = proxy.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        save.Release.SetResult();
        await configure;
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(proxy.Running);
    }

    private static async Task Finish(Task task, bool fail)
    {
        if (fail) await Assert.ThrowsAsync<IOException>(() => task);
        else await task;
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationOrExitDuringPreparationDrainsRollback(bool exit)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clash = new LifecycleClash();
        var calls = 0;
        clash.Prepare = async ct => {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
        };
        await using var proxy = new ProxyMonitor(clash);
        using var cancellation = new CancellationTokenSource();
        proxy.Start(false);
        var configure = proxy.ConfigureAsync(new(["JP"], ["B"]), () => throw new InvalidOperationException("Must not save"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await proxy.StopAsync();
        Task disposal = Task.CompletedTask;
        if (exit) disposal = proxy.DisposeAsync().AsTask(); else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => configure);
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, calls);
        Assert.False(proxy.Running);
    }

    [Fact]
    public async Task ConcurrentConfigurationsAreSerialized()
    {
        await using var proxy = new ProxyMonitor(new LifecycleClash());
        proxy.Start(false);
        var first = new SaveBarrier();
        var second = new SaveBarrier();
        var a = proxy.ConfigureAsync(new(["JP"], ["A"]), first.Save, default);
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var b = proxy.ConfigureAsync(new(["JP"], ["B"]), second.Save, default);
        Assert.False(second.Entered.Task.IsCompleted);
        await proxy.StopAsync();
        first.Release.SetResult();
        await a;
        await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        second.Release.SetResult();
        await b;
        Assert.Equal(new[] { "B" }, proxy.Preferences.SelectedSourceIds);
        Assert.False(proxy.Running);
    }

    [Fact]
    public async Task StartDuringDrainWaitsForOldConnectionCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clash = new LifecycleClash { Reset = async () => { entered.TrySetResult(); await release.Task; } };
        await using var proxy = new ProxyMonitor(clash);
        proxy.Start(false);
        var stopping = proxy.StopAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(stopping, proxy.StopAsync());
        proxy.Start(false);
        Assert.False(proxy.Running);
        release.SetResult();
        await stopping;
        Assert.True(proxy.Running);
    }
    private sealed class SaveBarrier(bool fail = false)
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Save() { Entered.SetResult(); await Release.Task; if (fail) throw new IOException("save failed"); }
    }
    private sealed class LifecycleClash : IClashService
    {
        public int Probes;
        public Func<CancellationToken, Task>? Prepare;
        public Func<Task>? Reset;
        public Task PrepareSourcesAsync(ProxyRegionPreferences p, CancellationToken ct, Action? changing = null) => Prepare?.Invoke(ct) ?? Task.CompletedTask;
        public Task ResetProbeConnectionsAsync(CancellationToken ct) => Reset?.Invoke() ?? Task.CompletedTask;
        public Task<ClashSnapshot> SnapshotAsync(CancellationToken ct) => Task.FromResult(Clash());
        public Task<ProxyProbe> ProbeProxyAsync(CancellationToken ct) { Interlocked.Increment(ref Probes); return Task.FromResult(new ProxyProbe(true)); }
        public Task<int> DelayAsync(string n, LocalEndpoint e, string u, CancellationToken ct) => Task.FromResult(1);
        public Task SelectAsync(string n, ClashSnapshot s, CancellationToken ct) => Task.CompletedTask;
    }
}
