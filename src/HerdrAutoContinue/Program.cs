using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Watchdog.Core;
using Watchdog.Infrastructure;

namespace HerdrAutoContinue;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            IHerdrService herdr = new LiveHerdrService();
            IClashService clash = new LiveClashService();
            var log = new JsonEventLog();
            IRegionPreferencesStore preferencesStore = new RegionPreferencesStore();
#if DEBUG
            // Test-only composition; release binaries cannot enter QA mode.
            if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SESSION") is { } qaSession)
            {
                if (!qaSession.StartsWith("watchdog-qa-", StringComparison.Ordinal)) throw new InvalidOperationException("QA session must be isolated");
                herdr = new FilteredHerdrService(herdr, qaSession);
                var qaDirectory = Environment.GetEnvironmentVariable("WATCHDOG_QA_SETTINGS_DIR") ?? Path.Combine(Path.GetTempPath(), qaSession);
                if (!Path.GetFileName(Path.GetFullPath(qaDirectory)).StartsWith("watchdog-qa-", StringComparison.Ordinal)) throw new InvalidOperationException("QA settings directory must be isolated");
                clash = new QaClashService(Environment.GetEnvironmentVariable("WATCHDOG_QA_REGIONS") == "recovery", qaDirectory);
                preferencesStore = new RegionPreferencesStore(Path.Combine(qaDirectory, "settings.json"));
                log = new JsonEventLog(Path.Combine(qaDirectory, "events.jsonl"));
            }
#endif
            var engine = new MonitorEngine(herdr, clash, log);
            var model = new MainViewModel(engine, log.Path, preferencesStore);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow(model);
            desktop.MainWindow = window;
#if DEBUG
            if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SESSION") is not null &&
                Environment.GetEnvironmentVariable("WATCHDOG_QA_AUTOMATION_DIR") is { } directory)
                window.Opened += async (_, _) => await QaAutomation.RunAsync(window, model, directory);
#endif
        }
        base.OnFrameworkInitializationCompleted();
    }
#if DEBUG
    private sealed class FilteredHerdrService(IHerdrService inner, string sessionName) : IHerdrService
    {
        public async Task<IReadOnlyList<Session>> SessionsAsync(CancellationToken ct) => (await inner.SessionsAsync(ct)).Where(s => s.Name == sessionName).ToArray();
        public Task<IReadOnlyList<Agent>> AgentsAsync(Session s, CancellationToken ct) => inner.AgentsAsync(s, ct);
        public Task<Observation> ObserveAsync(Agent a, Session s, bool refresh, CancellationToken ct) => inner.ObserveAsync(a, s, refresh, ct);
        public Task ResumeAsync(Observation o, CancellationToken ct) => inner.ResumeAsync(o, ct);
    }
#endif
}
