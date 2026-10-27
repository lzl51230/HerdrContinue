using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Watchdog.Core;
using Watchdog.Infrastructure;

namespace HerdrAutoContinue;

internal static class Program
{
    internal static LaunchOptions Options { get; private set; } = new();
    internal static AppInstance? Instance { get; private set; }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
#if DEBUG
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--qa-session") Environment.SetEnvironmentVariable("WATCHDOG_QA_SESSION", args[++i]);
                else if (args[i] == "--qa-settings-dir") Environment.SetEnvironmentVariable("WATCHDOG_QA_SETTINGS_DIR", args[++i]);
            }
#endif
            Options = LaunchOptions.Parse(args);
#if DEBUG
            if (args.Contains("--qa-login-test") && Options.StartHidden && Environment.GetEnvironmentVariable("WATCHDOG_QA_SETTINGS_DIR") is { } qaPath)
            {
                Environment.SetEnvironmentVariable("WATCHDOG_QA_SCENARIO", File.Exists(Path.Combine(qaPath, "expect-agent")) ? "startup-system-agent" : "startup-system");
                Environment.SetEnvironmentVariable("WATCHDOG_QA_AUTOMATION_DIR", Path.Combine(qaPath, "system-login"));
            }
#endif
            var scope = "production";
#if DEBUG
            if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SESSION") is { } qa)
            {
                if (!qa.StartsWith("watchdog-qa-", StringComparison.Ordinal)) throw new InvalidOperationException("QA session must be isolated");
                scope = qa;
            }
#endif
            using var instance = new AppInstance(scope);
            if (!instance.IsPrimary)
            {
                instance.NotifyAsync(activateWindow: !Options.StartHidden).GetAwaiter().GetResult();
                return 0;
            }
            Instance = instance;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception e)
        {
            new JsonEventLog().Append(new(DateTimeOffset.Now, "应用", "启动失败：" + e.Message));
            return 1;
        }
        finally { Instance = null; }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
        .With(new MacOSPlatformOptions { ShowInDock = !Options.StartHidden }).LogToTrace();
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
            IRegionPreferencesStore regions = new RegionPreferencesStore();
            IAppSettingsStore settings = new AppSettingsStore();
            string? qaDirectory = null;
            string? qaSession = null;
            var extraArguments = new List<string>();
#if DEBUG
            qaSession = Environment.GetEnvironmentVariable("WATCHDOG_QA_SESSION");
            if (qaSession is not null)
            {
                herdr = new FilteredHerdrService(herdr, qaSession);
                qaDirectory = Environment.GetEnvironmentVariable("WATCHDOG_QA_SETTINGS_DIR") ?? Path.Combine(Path.GetTempPath(), qaSession);
                if (!Path.GetFileName(Path.GetFullPath(qaDirectory)).StartsWith("watchdog-qa-", StringComparison.Ordinal)) throw new InvalidOperationException("QA settings directory must be isolated");
                clash = new QaClashService(Environment.GetEnvironmentVariable("WATCHDOG_QA_REGIONS") == "recovery", qaDirectory);
                regions = new RegionPreferencesStore(Path.Combine(qaDirectory, "settings.json"));
                settings = new AppSettingsStore(Path.Combine(qaDirectory, "app-settings.json"));
                log = new JsonEventLog(Path.Combine(qaDirectory, "events.jsonl"));
                extraArguments.AddRange(["--qa-session", qaSession, "--qa-settings-dir", qaDirectory]);
                if ((Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO") ?? "").StartsWith("startup", StringComparison.Ordinal)) extraArguments.Add("--qa-login-test");
            }
#endif
            var command = AutoStartCommand.ForCurrentProcess(extraArguments);
            var registrationName = qaSession is null ? "HerdrAutoContinue" : "HerdrAutoContinue-QA-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(qaSession)))[..12];
            IAutoStartRegistration registration = OperatingSystem.IsWindows()
                ? new WindowsAutoStartRegistration(command, registrationName)
                : new MacAutoStartRegistration(command, label: qaSession is null ? "local.alex.herdr-auto-continue" : "local.alex." + registrationName,
                    logPath: qaDirectory is null ? null : Path.Combine(qaDirectory, "startup.log"));
            var controller = new AppSettingsController(settings, registration);
            var model = new MainViewModel(new MonitorEngine(herdr, clash, log), log.Path, regions);
            var host = new DesktopApplicationController(this, desktop, model, controller, Program.Options, Program.Instance, log);
            host.Start();
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
