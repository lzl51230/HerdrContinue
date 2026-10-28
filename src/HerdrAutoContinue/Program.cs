using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Markup.Xaml.Styling;
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
            LoadQaBundleSettings();
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--qa-session") Environment.SetEnvironmentVariable("WATCHDOG_QA_SESSION", args[++i]);
                else if (args[i] == "--qa-settings-dir") Environment.SetEnvironmentVariable("WATCHDOG_QA_SETTINGS_DIR", args[++i]);
            }
#endif
            Options = LaunchOptions.Parse(args);
#if DEBUG
            if (args.Contains("--qa-login-test") && Options.IsAutoStart && Environment.GetEnvironmentVariable("WATCHDOG_QA_SETTINGS_DIR") is { } qaPath)
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
                instance.NotifyAsync(activateWindow: !Options.IsAutoStart).GetAwaiter().GetResult();
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
        .With(new MacOSPlatformOptions { ShowInDock = false }).LogToTrace();
#if DEBUG
    private static void LoadQaBundleSettings()
    {
        if (!OperatingSystem.IsMacOS() || Environment.ProcessPath is not { } executable) return;
        var contents = Directory.GetParent(Path.GetDirectoryName(executable)!)?.FullName;
        if (contents is null) return;
        var file = Path.Combine(contents, "Resources", "watchdog-qa.json");
        if (!File.Exists(file)) return;
        var info = System.Xml.Linq.XDocument.Load(Path.Combine(contents, "Info.plist"));
        var bundleId = info.Descendants("key").SingleOrDefault(k => k.Value == "CFBundleIdentifier")?.NextNode as System.Xml.Linq.XElement;
        if (bundleId?.Value.StartsWith("local.alex.herdr-auto-continue.qa.", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("QA bundle must have an isolated identifier");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        foreach (var property in document.RootElement.EnumerateObject())
            if (property.Name.StartsWith("WATCHDOG_QA_", StringComparison.Ordinal) && Environment.GetEnvironmentVariable(property.Name) is null)
                Environment.SetEnvironmentVariable(property.Name, property.Value.GetString());
    }
#endif
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://HerdrAutoContinue/")) { Source = new Uri("avares://HerdrAutoContinue/ThemeStyles.axaml") });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MacLaunchContext.Install(Program.Options);
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
                : OperatingSystem.IsMacOS() ? new MacLoginItemRegistration(command, new MacAppServices(),
                    legacyPath: qaDirectory is null ? null : Path.Combine(qaDirectory, "legacy-login.plist"),
                    legacyLabel: qaSession is null ? "local.alex.herdr-auto-continue" : "local.alex." + registrationName)
                : throw new PlatformNotSupportedException("当前系统不支持桌面登录项。");
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
