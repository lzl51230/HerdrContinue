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
    private readonly CheckBox enabled;
    private readonly TextBlock message;
    private readonly Button apply, cancel;
    private bool saving;
    private Task pending = Task.CompletedTask;
    public AppSettingsWindow(AppSettingsController controller)
    {
        this.controller = controller;
        Title = "设置"; Width = 560; Height = 350; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "app-settings-window");
        enabled = new CheckBox { Content = "登录后自动启动并运行续接服务", IsChecked = controller.Current.Settings.AutoStartEnabled };
        AutomationProperties.SetAutomationId(enabled, "startup-enabled");
        message = new TextBlock { Text = controller.Current.Warning ?? controller.AvailabilityError ?? "", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
        AutomationProperties.SetAutomationId(message, "app-settings-message");
        apply = new Button { Content = "应用并保存", Background = Brush.Parse("#087F8C"), Foreground = Brushes.White };
        cancel = new Button { Content = "取消" };
        AutomationProperties.SetAutomationId(apply, "settings-apply"); AutomationProperties.SetAutomationId(cancel, "settings-cancel");
        var root = new Grid { Margin = new(24), RowDefinitions = new("Auto,Auto,Auto,*,Auto"), RowSpacing = 16 };
        root.Children.Add(new TextBlock { Text = "通用设置", FontSize = 22, FontWeight = FontWeight.SemiBold });
        Grid.SetRow(enabled, 1); root.Children.Add(enabled);
        var help = new TextBlock { Text = "系统登录后静默启动；开启后，手动启动新进程也会自动运行服务。\n关闭主窗口后继续在托盘／菜单栏运行，可从菜单停止服务或退出。\n请将应用保存在固定位置，移动后需重新开启自启动。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
        Grid.SetRow(help, 2); root.Children.Add(help); Grid.SetRow(message, 3); root.Children.Add(message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, apply } };
        Grid.SetRow(actions, 4); root.Children.Add(actions); Content = root;
        cancel.Click += (_, _) => Close();
        apply.Click += async (_, _) => { if (!saving) { pending = SaveAsync(); await pending; } };
        Closing += (_, e) => { if (saving) e.Cancel = true; };
    }
    public Task WaitForSaveAsync() => pending;
    private async Task SaveAsync()
    {
        saving = true; apply.IsEnabled = cancel.IsEnabled = enabled.IsEnabled = false;
        try
        {
            await controller.ApplyAsync(new AppSettings(enabled.IsChecked == true));
            saving = false; Close();
        }
        catch (Exception e) { message.Text = "保存失败：" + e.Message; enabled.IsChecked = controller.Current.Settings.AutoStartEnabled; }
        finally { saving = false; apply.IsEnabled = cancel.IsEnabled = enabled.IsEnabled = true; }
    }
}
