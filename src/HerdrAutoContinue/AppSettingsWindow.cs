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
    private readonly bool originalAutoStart;
    private readonly ComboBox theme;
    private readonly CheckBox enabled;
    private readonly TextBlock message;
    private readonly Button apply, cancel;
    private bool saving;
    private Task pending = Task.CompletedTask;
    public AppSettingsWindow(AppSettingsController controller, AppThemeManager themes)
    {
        this.controller = controller; this.themes = themes;
        originalAutoStart = controller.Current.Settings.AutoStartEnabled;
        Title = "设置"; Width = 600; Height = 480; MinWidth = 480; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "app-settings-window");
        enabled = new CheckBox { Content = "登录后自动启动并运行续接服务", IsChecked = controller.Current.Settings.AutoStartEnabled };
        AutomationProperties.SetAutomationId(enabled, "startup-enabled");
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
        var help = new TextBlock { Text = "系统登录后静默启动；开启后，手动启动新进程也会自动运行服务。\n关闭主窗口后继续在托盘／菜单栏运行，可从菜单停止服务或退出。\n请将应用保存在固定位置，移动后需重新开启自启动。", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        AppThemeManager.Bind(previewHelp, TextBlock.ForegroundProperty, "AppSecondary");
        AppThemeManager.Bind(help, TextBlock.ForegroundProperty, "AppSecondary");
        var fields = new StackPanel { Spacing = 14, Children = {
            new TextBlock { Text = "颜色主题", FontWeight = FontWeight.SemiBold }, theme, previewHelp,
            new Border { Height = 1, Margin = new(0, 4) }, enabled, help, message } };
        AppThemeManager.Bind((Border)fields.Children[3], Border.BackgroundProperty, "AppBorder");
        var scroll = new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, apply } };
        Grid.SetRow(actions, 2); root.Children.Add(actions); Content = root;
        cancel.Click += (_, _) => Close();
        apply.Click += async (_, _) => { if (!saving) { pending = SaveAsync(); await pending; } };
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        Closed += (_, _) => themes.CancelPreview();
    }
    public Task WaitForSaveAsync() => pending;
    private async Task SaveAsync()
    {
        saving = true; apply.IsEnabled = cancel.IsEnabled = enabled.IsEnabled = theme.IsEnabled = false;
        try
        {
            var selected = ((ThemeChoice)theme.SelectedItem!).Theme;
            var autoStart = enabled.IsChecked == true;
            await controller.ApplyChangeAsync(new(selected, autoStart == originalAutoStart ? null : autoStart));
            themes.Commit(controller.Current.Settings.Theme);
            saving = false; Close();
        }
        catch (Exception e)
        {
            themes.CancelPreview();
            theme.SelectedItem = AppThemeManager.Choices.Single(c => c.Theme == themes.Saved);
            message.Text = "保存失败：" + e.Message; enabled.IsChecked = controller.Current.Settings.AutoStartEnabled;
        }
        finally { saving = false; apply.IsEnabled = cancel.IsEnabled = enabled.IsEnabled = theme.IsEnabled = true; }
    }
}
