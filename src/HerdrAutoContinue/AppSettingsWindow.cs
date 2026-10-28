using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Watchdog.Core;
using Watchdog.Infrastructure;

namespace HerdrAutoContinue;

public sealed class AppSettingsWindow : Window
{
    private readonly AppSettingsController controller;
    private readonly AppThemeManager themes;
    private bool originalAutoStart;
    private readonly ComboBox theme;
    private readonly CheckBox enabled, silent;
    private readonly TextBlock message;
    private readonly Button apply, cancel;
    private readonly TextBlock startupStatus;
    private readonly Button systemSettings;
    private bool refreshing;
    private bool saving;
    private Task pending = Task.CompletedTask;
    public AppSettingsWindow(AppSettingsController controller, AppThemeManager themes)
    {
        this.controller = controller; this.themes = themes;
        originalAutoStart = DisplayedAutoStart();
        Title = "设置"; Width = 600; Height = 480; MinWidth = 480; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "app-settings-window");
        enabled = new CheckBox { Content = "登录后自动启动并运行续接服务", IsChecked = originalAutoStart };
        AutomationProperties.SetAutomationId(enabled, "startup-enabled");
        silent = new CheckBox { Content = "静默启动", IsChecked = controller.Current.Settings.SilentStartEnabled };
        AutomationProperties.SetAutomationId(silent, "silent-start-enabled");
        message = new TextBlock { Text = controller.Current.Warning ?? controller.AvailabilityError ?? "", TextWrapping = TextWrapping.Wrap };
        AppThemeManager.Bind(message, TextBlock.ForegroundProperty, "AppError");
        AutomationProperties.SetAutomationId(message, "app-settings-message");
        apply = new Button { Content = "应用并保存", Classes = { "accent" } };
        cancel = new Button { Content = "取消" };
        AutomationProperties.SetAutomationId(apply, "settings-apply"); AutomationProperties.SetAutomationId(cancel, "settings-cancel");
        var root = new Grid { Margin = new(24), RowDefinitions = new("Auto,*,Auto"), RowSpacing = 20 };
        root.Children.Add(new TextBlock { Text = "通用设置", FontSize = 22, FontWeight = FontWeight.SemiBold });
        theme = new ComboBox { ItemsSource = AppThemeManager.Choices, HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedItem = AppThemeManager.Choices.Single(c => c.Theme == controller.Current.Settings.Theme) };
        AutomationProperties.SetAutomationId(theme, "color-theme");
        theme.SelectionChanged += (_, _) => { if (theme.SelectedItem is ThemeChoice choice) themes.Preview(choice.Theme); };
        var previewHelp = new TextBlock { Text = "选择后立即预览；应用并保存后保留，取消则恢复原主题。", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var silentHelp = new TextBlock { Text = "勾选后，手动或登录启动时不显示主界面；应用已在后台运行时再次打开可显示窗口。", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        AppThemeManager.Bind(silentHelp, TextBlock.ForegroundProperty, "AppSecondary");
        var help = new TextBlock { Text = "手动打开新进程始终运行服务；登录自启动控制登录后是否运行应用。\n关闭主窗口后继续在托盘／菜单栏运行，可从菜单停止服务或退出。\n请将应用保存在固定位置，移动后需重新开启自启动。", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        AppThemeManager.Bind(previewHelp, TextBlock.ForegroundProperty, "AppSecondary");
        AppThemeManager.Bind(help, TextBlock.ForegroundProperty, "AppSecondary");
        startupStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        AppThemeManager.Bind(startupStatus, TextBlock.ForegroundProperty, "AppSecondary");
        AutomationProperties.SetAutomationId(startupStatus, "startup-system-status");
        systemSettings = new Button { IsVisible = controller.CanOpenSettings, Content = "在系统设置中查看" };
        AutomationProperties.SetAutomationId(systemSettings, "startup-system-settings");
        systemSettings.Click += (_, _) =>
        {
            if (controller.Current.Registration.Status == AutoStartState.RequiresApproval) controller.PromptForApproval();
            else controller.OpenSettings();
        };
        UpdateStatus();
        var fields = new StackPanel { Spacing = 14, Children = {
            new TextBlock { Text = "颜色主题", FontWeight = FontWeight.SemiBold }, theme, previewHelp,
            new Border { Height = 1, Margin = new(0, 4) }, enabled, help, startupStatus, systemSettings, silent, silentHelp, message } };
        AppThemeManager.Bind((Border)fields.Children[3], Border.BackgroundProperty, "AppBorder");
        var scroll = new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, apply } };
        Grid.SetRow(actions, 2); root.Children.Add(actions); Content = root;
        cancel.Click += (_, _) => Close();
        apply.Click += async (_, _) => { if (!saving) { pending = SaveAsync(); await pending; } };
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        Closed += (_, _) => themes.CancelPreview();
        Activated += async (_, _) => await RefreshStatusAsync();
    }
    public Task WaitForSaveAsync() => pending;
    private bool DisplayedAutoStart() => controller.Current.Registration.Status == AutoStartState.RequiresApproval
        ? controller.Current.RequestedSettings.AutoStartEnabled : controller.Current.Settings.AutoStartEnabled;
    private void UpdateStatus()
    {
        startupStatus.Text = controller.Current.Registration.Status switch
        {
            AutoStartState.Enabled => controller.CanOpenSettings ? "系统登录项：已启用" : "系统启动项：已登记，可在 Windows“启动应用”中查看启用状态",
            AutoStartState.RequiresApproval => "系统登录项：等待用户许可，尚未生效",
            AutoStartState.NotRegistered => "系统启动项：未登记或已关闭",
            _ => "系统启动项：不可用，请检查应用安装位置与系统状态"
        };
        systemSettings.Content = controller.Current.Registration.Status == AutoStartState.RequiresApproval ? "处理登录项许可" : "在系统设置中查看";
    }
    private async Task RefreshStatusAsync()
    {
        if (saving || refreshing) return;
        refreshing = true;
        try
        {
            var unchanged = (enabled.IsChecked == true) == originalAutoStart;
            await controller.InitializeAsync();
            if (unchanged) { originalAutoStart = DisplayedAutoStart(); enabled.IsChecked = originalAutoStart; }
            UpdateStatus();
            message.Text = controller.Current.Warning ?? controller.AvailabilityError ?? "";
        }
        catch (Exception e) { message.Text = "状态读取失败：" + e.Message; }
        finally { refreshing = false; }
    }
    private async Task SaveAsync()
    {
        var prompt = false;
        saving = true; apply.IsEnabled = cancel.IsEnabled = enabled.IsEnabled = silent.IsEnabled = theme.IsEnabled = systemSettings.IsEnabled = false;
        try
        {
            var selected = ((ThemeChoice)theme.SelectedItem!).Theme;
            var autoStart = enabled.IsChecked == true;
            await controller.ApplyChangeAsync(new(selected, autoStart == originalAutoStart ? null : autoStart, silent.IsChecked == true));
            themes.Commit(controller.Current.Settings.Theme);
            prompt = autoStart && autoStart != originalAutoStart && controller.Current.Registration.Status == AutoStartState.RequiresApproval;
            originalAutoStart = DisplayedAutoStart();
            if (prompt) { UpdateStatus(); message.Text = controller.Current.Warning; }
            else { saving = false; Close(); }
        }
        catch (Exception e)
        {
            themes.CancelPreview();
            theme.SelectedItem = AppThemeManager.Choices.Single(c => c.Theme == themes.Saved);
            message.Text = "保存失败：" + e.Message; enabled.IsChecked = DisplayedAutoStart();
            originalAutoStart = DisplayedAutoStart(); silent.IsChecked = controller.Current.Settings.SilentStartEnabled; UpdateStatus();
        }
        finally { saving = false; apply.IsEnabled = cancel.IsEnabled = enabled.IsEnabled = silent.IsEnabled = theme.IsEnabled = systemSettings.IsEnabled = true; }
        if (prompt) controller.PromptForApproval();
    }
}
