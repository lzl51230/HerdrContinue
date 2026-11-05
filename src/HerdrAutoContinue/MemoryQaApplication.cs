#if DEBUG
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Watchdog.Core;

namespace HerdrAutoContinue;

// A separate application lifetime: never discovers Herdr, changes Clash, or loads user settings.
internal sealed class MemoryQaApplication : Application
{
    private static string directory = "";
    private static bool uiOnly;
    private static int soakSeconds;
    private readonly Dictionary<string, object?> result = new();
    [DllImport("libHerdrMacIntegration.dylib")]
    private static extern int hdac_qa_walk_accessibility();
    [DllImport("libHerdrMacIntegration.dylib")]
    private static extern ulong hdac_qa_physical_footprint();

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Native memory QA requires macOS.");
        directory = Path.GetFullPath(args[Array.IndexOf(args, "--qa-memory") + 1]);
        uiOnly = args.Contains("--qa-memory-ui-only");
        var durationIndex = Array.IndexOf(args, "--qa-memory-seconds");
        soakSeconds = durationIndex >= 0 ? int.Parse(args[durationIndex + 1]) : 0;
        Directory.CreateDirectory(directory);
        return AppBuilder.Configure<MemoryQaApplication>().UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://HerdrAutoContinue/")) { Source = new Uri("avares://HerdrAutoContinue/ThemeStyles.axaml") });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var content = new ContentControl();
        var window = new Window { Title = "Herdr memory regression QA", Width = 640, Height = 400, Content = content };
        desktop.MainWindow = window;
        var started = false;
        window.Opened += async (_, _) =>
        {
            if (started) return;
            started = true;
            var exitCode = 0;
            try
            {
                if (!uiOnly)
                {
                    List<WeakReference> retired = [];
                    for (var i = 0; i < 5; i++)
                    {
                        retired.Add(CreateView(content, i));
                        await Task.Delay(100);
                        if (hdac_qa_walk_accessibility() < 3) throw new InvalidOperationException("Accessibility traversal did not reach controls.");
                        content.Content = null;
                        await Task.Delay(100);
                        hdac_qa_walk_accessibility();
                    }
                    for (var i = 0; i < 4; i++)
                    {
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        await Task.Delay(100);
                    }
                    var alive = retired.Count(r => r.IsAlive);
                    result["retiredViews"] = retired.Count;
                    result["retainedViews"] = alive;
                    if (alive != 0) throw new InvalidOperationException($"Native accessibility retained {alive}/{retired.Count} detached views.");
                    await CheckReattachmentAsync(window, content);
                }
                await CheckSnapshotsAsync();
                if (soakSeconds > 0) await SoakAsync(window, desktop);
                result["success"] = true;
            }
            catch (Exception e) { result["error"] = e.ToString(); result["success"] = false; exitCode = 1; }
            finally
            {
                using var process = Process.GetCurrentProcess();
                result["workingSetBytes"] = process.WorkingSet64;
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                desktop.Shutdown(exitCode);
            }
        };
        base.OnFrameworkInitializationCompleted();
    }
    private async Task CheckReattachmentAsync(Window window, ContentControl content)
    {
        // Keep the managed control alive while its native element may be released.
        var view = new StackPanel { Children = { new Button { Content = "Reattached control" } } };
        for (var i = 0; i < 10; i++)
        {
            content.Content = view;
            await Task.Delay(30);
            hdac_qa_walk_accessibility();
            content.Content = null;
            await Task.Delay(30);
            hdac_qa_walk_accessibility();
            window.Hide();
            view.Children.Add(new TextBlock { Text = $"Update while hidden {i}" });
            window.Show();
        }
        content.Content = view;
        await Task.Delay(50);
        if (hdac_qa_walk_accessibility() < 3) throw new InvalidOperationException("Reattached accessibility elements are missing.");
        content.Content = null;
        result["livePeerReattachment"] = true;
    }
    private async Task SoakAsync(Window initial, IClassicDesktopStyleApplicationLifetime desktop)
    {
        using var themes = new AppThemeManager(this);
        await using var model = new MainViewModel(new MonitorEngine(new NoHerdr(), new NoClash()), "unused");
        var window = new MainWindow(model, () => true, () => Task.CompletedTask, () => Task.CompletedTask);
        desktop.MainWindow = window;
        initial.Close();
        window.Show();
        var events = Enumerable.Range(0, 500).Select(i => new MonitorEvent(DateTimeOffset.UnixEpoch.AddSeconds(i), "QA", $"Event {i}")).ToList();
        var snapshot = new MonitorSnapshot(true, "Isolated memory soak", DateTimeOffset.Now,
            Enumerable.Range(0, 12).Select(i => new AgentRow(i.ToString(), $"Agent {i}", "QA", "codex", "working", "运行中", null, 0)).ToArray(), events.ToArray(), new ProxyStatus());
        var watch = Stopwatch.StartNew();
        var nextSample = TimeSpan.Zero;
        var iteration = 0;
        var phase = -1;
        ulong? baseline = null;
        ulong footprint = 0;
        using var samples = new StreamWriter(Path.Combine(directory, "samples.jsonl")) { AutoFlush = true };
        while (watch.Elapsed.TotalSeconds < soakSeconds)
        {
            if (iteration % 5 == 0)
            {
                events.RemoveAt(0);
                events.Add(new(DateTimeOffset.Now, "Memory QA", $"New event {iteration}"));
            }
            model.QaPublishSnapshot(snapshot with { LastScan = DateTimeOffset.Now, Events = events.ToArray(), Connection = $"Iteration {iteration}" });
            var currentPhase = (int)(watch.Elapsed.TotalSeconds / 30) % 4;
            if (currentPhase != phase)
            {
                phase = currentPhase;
                if (phase == 2) window.Hide();
                else
                {
                    window.Show();
                    window.WindowState = phase == 1 ? WindowState.Minimized : WindowState.Normal;
                }
            }
            await Task.Delay(100);
            if (watch.Elapsed >= nextSample)
            {
                var nodes = hdac_qa_walk_accessibility();
                footprint = hdac_qa_physical_footprint();
                if (footprint == 0) throw new InvalidOperationException("Could not measure physical footprint.");
                if (watch.Elapsed.TotalSeconds >= Math.Min(600, soakSeconds / 3.0)) baseline ??= footprint;
                samples.WriteLine(JsonSerializer.Serialize(new { seconds = watch.Elapsed.TotalSeconds, footprintBytes = footprint, managedBytes = GC.GetTotalMemory(false), accessibilityNodes = nodes, phase, iteration }));
                if (footprint > 2UL * 1024 * 1024 * 1024) throw new InvalidOperationException("Memory soak exceeded 2 GiB; stopping early.");
                nextSample = watch.Elapsed + TimeSpan.FromSeconds(5);
            }
            iteration++;
        }
        window.Show(); window.WindowState = WindowState.Normal;
        await DrainAsync();
        if (model.Events.Count != 500 || model.Events[0] != events[^1]) throw new InvalidOperationException("Restored window did not show latest bounded events.");
        result["soakSeconds"] = watch.Elapsed.TotalSeconds;
        result["soakIterations"] = iteration;
        result["physicalFootprintBytes"] = footprint;
        var growth = (long)footprint - (long)(baseline ?? footprint);
        result["postWarmupGrowthBytes"] = growth;
        if (growth > 100 * 1024 * 1024) throw new InvalidOperationException("Post-warmup footprint grew by more than 100 MiB.");
        window.Close();
    }
    private async Task CheckSnapshotsAsync()
    {
        var model = new MainViewModel(new MonitorEngine(new NoHerdr(), new NoClash()), "unused");
        try
        {
            var events = Enumerable.Range(0, 500).Select(i => new MonitorEvent(DateTimeOffset.UnixEpoch.AddSeconds(i), "QA", $"Event {i}")).ToArray();
            var snapshot = new MonitorSnapshot(false, "QA", null, [], events, new ProxyStatus
            {
                Delays = [new("Japan", 100, "JP")]
            });
            model.QaPublishSnapshot(snapshot);
            await DrainAsync();
            var notifications = 0;
            model.Events.CollectionChanged += (_, _) => notifications++;
            model.Delays.CollectionChanged += (_, _) => notifications++;
            for (var i = 0; i < 3; i++) { model.QaPublishSnapshot(snapshot); await DrainAsync(); }
            if (notifications != 0) throw new InvalidOperationException($"Identical snapshots raised {notifications} collection notifications.");
            result["unchangedCollectionsQuiet"] = true;
            var updated = snapshot with { Events = [.. events.Skip(1), new(DateTimeOffset.UnixEpoch.AddSeconds(500), "QA", "New event")] };
            model.QaPublishSnapshot(updated);
            await DrainAsync();
            if (model.Events.Count != 500 || model.Events[0].Message != "New event" || notifications != 2)
                throw new InvalidOperationException("Appending one event must insert one row and remove only the oldest row.");
            result["boundedIncrementalEvents"] = true;
            var applies = 0;
            model.PropertyChanged += (_, e) => { if (e.PropertyName == "") applies++; };
            for (var i = 0; i < 10000; i++) model.QaPublishSnapshot(updated with { Connection = i.ToString() });
            await DrainAsync();
            if (applies != 1 || model.Connection != "9999") throw new InvalidOperationException($"Snapshot burst applied {applies} times; expected latest only.");
            result["snapshotBurstCoalesced"] = true;
            model.QaPublishSnapshot(updated with { Connection = "must not apply" });
            await model.DisposeAsync();
            model.QaPublishSnapshot(snapshot);
            await DrainAsync();
            if (model.Connection != "9999") throw new InvalidOperationException("A queued snapshot was applied after disposal.");
            result["disposedSnapshotsDiscarded"] = true;
        }
        finally { await model.DisposeAsync(); }
    }
    private static async Task DrainAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.SystemIdle);

    private sealed class NoHerdr : IHerdrService
    {
        public Task<IReadOnlyList<Session>> SessionsAsync(CancellationToken ct) => throw new InvalidOperationException("QA must not discover real sessions.");
        public Task<IReadOnlyList<Agent>> AgentsAsync(Session s, CancellationToken ct) => throw new InvalidOperationException();
        public Task<Observation> ObserveAsync(Agent a, Session s, bool refresh, CancellationToken ct) => throw new InvalidOperationException();
        public Task ResumeAsync(Observation o, CancellationToken ct) => throw new InvalidOperationException();
    }
    private sealed class NoClash : IClashService
    {
        public Task<ClashSnapshot> SnapshotAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<ProxyProbe> ProbeProxyAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<int> DelayAsync(string node, LocalEndpoint endpoint, string url, CancellationToken ct) => throw new InvalidOperationException();
        public Task SelectAsync(string node, ClashSnapshot snapshot, CancellationToken ct) => throw new InvalidOperationException();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateView(ContentControl content, int iteration)
    {
        var view = new StackPanel
        {
            DataContext = new byte[1024 * 1024],
            Children = { new TextBlock { Text = $"Retired view {iteration}" }, new Button { Content = "Accessibility lifetime" } }
        };
        content.Content = view;
        return new WeakReference(view);
    }
}
#endif
