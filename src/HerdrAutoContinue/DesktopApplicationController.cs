using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Watchdog.Core;
using Watchdog.Infrastructure;

namespace HerdrAutoContinue;

public sealed class DesktopApplicationController
{
    private readonly Application app;
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly MainViewModel model;
    private readonly AppSettingsController settings;
    private LaunchOptions options;
    private readonly IEventSink log;
    private readonly AppThemeManager themes;
    private bool themeReady, showWhenReady;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TrayIcon tray;
    private readonly NativeMenuItem toggle, status;
    private MainWindow? window;
    private AppSettingsWindow? settingsWindow;
    private bool openingSettings;
    private Task startup = Task.CompletedTask;
    private Task? exiting;
    public bool IsExiting { get; private set; }

    public DesktopApplicationController(Application app, IClassicDesktopStyleApplicationLifetime desktop, MainViewModel model,
        AppSettingsController settings, LaunchOptions options, AppInstance? instance, IEventSink log)
    {
        this.app = app; this.desktop = desktop; this.model = model; this.settings = settings; this.options = options; this.log = log;
#if DEBUG
        themes = new(app, (Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO") ?? "").StartsWith("theme", StringComparison.Ordinal) ? ThemeQaAutomation.Platform : null);
#else
        themes = new(app);
#endif
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var show = new NativeMenuItem("打开主窗口"); show.Click += (_, _) => ShowMainWindow();
        var preferences = new NativeMenuItem("设置"); preferences.Click += async (_, _) => await ShowSettingsAsync();
        toggle = new NativeMenuItem("启动服务"); toggle.Click += async (_, _) => { if (!IsExiting) await model.ToggleAsync(); };
        var quit = new NativeMenuItem("退出"); quit.Click += async (_, _) => await QuitAsync();
        status = new NativeMenuItem("正在初始化…") { IsEnabled = false };
        using var icon = typeof(MainWindow).Assembly.GetManifestResourceStream("HerdrAutoContinue.AppIcon.ico")!;
        tray = new TrayIcon { Icon = new WindowIcon(icon), ToolTipText = "Herdr 自动续接", Menu = new NativeMenu { Items = { status, new NativeMenuItemSeparator(), show, preferences, toggle, new NativeMenuItemSeparator(), quit } } };
        tray.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(app, new TrayIcons { tray });
        model.PropertyChanged += ModelChanged;
        desktop.ShutdownRequested += (_, e) => e.Cancel = RequestShutdown();
        if (app.TryGetFeature<IActivatableLifetime>() is { } activatable)
            activatable.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) ShowMainWindow(); };
        instance?.SetActivationHandler(() => Dispatcher.UIThread.Post(ShowMainWindow));
    }
    public void Start()
    {
        startup = StartCoreAsync();
#if DEBUG
        if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SESSION") is not null &&
            Environment.GetEnvironmentVariable("WATCHDOG_QA_AUTOMATION_DIR") is { } directory)
            _ = RunQaAsync(directory);
#endif
    }
    private async Task StartCoreAsync()
    {
        try
        {
            options = await MacLaunchContext.ResolveAsync(options, lifetime.Token);
            var loaded = await settings.InitializeAsync(lifetime.Token);
            await model.ApplyAppSettingsAsync(loaded.Settings);
            showWhenReady |= options.ShouldShowMainWindow(loaded.Settings);
            themes.Commit(loaded.Settings.Theme);
            themeReady = true;
            if (showWhenReady) ShowMainWindow();
            await model.InitializeAsync();
            if (loaded.Warning is { } warning) Report(warning);
            if (IsExiting) return;
            if (options.ShouldStartService(loaded.Settings)) await model.StartServiceAsync();
            else if (loaded.Settings.ProxyRecoveryEnabled) await model.RefreshRegionsAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            Report("应用初始化失败：" + e.Message);
            themeReady = true; if (showWhenReady) ShowMainWindow();
        }
    }
    private void Report(string message)
    {
        log.Append(new(DateTimeOffset.Now, "应用", message)); model.SetError(message);
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        toggle.Header = model.Running ? "停止服务" : "启动服务"; toggle.IsEnabled = !IsExiting && model.CanToggle;
        status.Header = model.Running ? model.FeatureSummary : "服务已停止";
        tray.ToolTipText = "Herdr 自动续接 · " + (model.Running ? model.FeatureSummary : "已停止");
    }
    public void ShowMainWindow()
    {
        if (IsExiting) return;
        if (!themeReady) { showWhenReady = true; return; }
        if (window is null)
        {
            window = new(model, () => IsExiting, ShowSettingsAsync, QuitAsync);
            desktop.MainWindow = window;
        }
        app.TryGetFeature<IActivatableLifetime>()?.TryLeaveBackground();
        MacLaunchContext.Reveal();
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
    public async Task ShowSettingsAsync()
    {
        if (IsExiting) return;
        ShowMainWindow();
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        if (openingSettings) return;
        openingSettings = true;
        try
        {
            await settings.InitializeAsync(lifetime.Token);
            if (IsExiting) return;
            themes.Commit(settings.Current.Settings.Theme);
            settingsWindow = new(settings, themes, model.ApplyAppSettingsAsync);
            await settingsWindow.ShowDialog(window!);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) { Report("无法打开设置：" + e.Message); }
        finally { settingsWindow = null; openingSettings = false; }
    }
    public Task QuitAsync() => exiting ??= QuitCoreAsync();
    private bool RequestShutdown()
    {
        if (IsExiting) return false;
        _ = QuitAsync();
        // Native shutdown requests can include system logout; never prevent them.
        return false;
    }
    private async Task QuitCoreAsync()
    {
        IsExiting = true; lifetime.Cancel();
        if (window is not null) window.IsEnabled = false;
        var stopping = model.DisposeAsync().AsTask();
        if (settingsWindow is not null) await settingsWindow.WaitForSaveAsync();
        await stopping;
        await startup;
        model.PropertyChanged -= ModelChanged;
        tray.Dispose(); themes.Dispose(); lifetime.Dispose();
        MacLaunchContext.Restore();
#if DEBUG
        QaStopped?.Invoke();
#endif
        desktop.Shutdown();
    }
#if DEBUG
    private async Task RunQaAsync(string directory)
    {
        await startup;
        if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO")?.StartsWith("features", StringComparison.Ordinal) == true)
            await FeatureQaAutomation.RunAsync(this, model, settings, directory);
        else if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO")?.StartsWith("cooldown", StringComparison.Ordinal) == true)
            await CooldownQaAutomation.RunAsync(this, model, settings, directory);
        else if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO") is { } nativeScenario && nativeScenario.StartsWith("native-", StringComparison.Ordinal))
            await NativeLoginQaAutomation.RunAsync(this, model, settings, directory, options);
        else if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO") is { } scenario && scenario.StartsWith("theme", StringComparison.Ordinal))
            await ThemeQaAutomation.RunAsync(this, model, settings, themes, directory, options);
        else if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO") is { } startupScenario && startupScenario.StartsWith("startup", StringComparison.Ordinal))
            await StartupQaAutomation.RunAsync(this, model, settings, directory, options);
        else if (window is not null) await QaAutomation.RunAsync(window, model, directory);
    }
    internal MainWindow? QaWindow => window;
    internal bool QaTrayVisible => tray.IsVisible;
    internal AppSettingsWindow? QaSettingsWindow => settingsWindow;
    internal event Action? QaStopped;
    internal bool QaRequestSystemShutdown() => RequestShutdown();
#endif
}
