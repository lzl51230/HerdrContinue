using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Threading;
using Watchdog.Infrastructure;

namespace HerdrAutoContinue;

public sealed class UpdatePanel : StackPanel
{
    public UpdatePanel(AppUpdates updates, Func<Task> install, bool compact = false)
    {
        Spacing = 6;
        var label = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var check = new Button { Content = "检查更新" };
        var update = new Button { Content = "更新并重启" };
        var release = new Button { Content = "查看发布说明" };
        release.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/lzl51230/HerdrContinue/releases/latest") { UseShellExecute = true });
        var cancel = new Button { Content = "取消下载" };
        AutomationProperties.SetAutomationId(label, "update-status");
        AutomationProperties.SetAutomationId(check, "update-check");
        AutomationProperties.SetAutomationId(update, "update-install");
        Children.Add(label);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { check, update, cancel, release } });
        void Refresh() => Dispatcher.UIThread.Post(() => {
            label.Text = (updates.RestartMessage is { } result ? result + "\n" : "") + "版本 " + AppUpdates.CurrentVersion + " · " + updates.Status;
            check.IsEnabled = !updates.Busy;
            update.IsVisible = updates.Available is not null; update.IsEnabled = !updates.Busy;
            cancel.IsVisible = updates.Busy && !updates.Installing && updates.Available is not null;
            IsVisible = !compact || updates.RestartMessage is not null || updates.Available is not null || updates.Installing;
        });
        check.Click += async (_, _) => await updates.CheckAsync();
        update.Click += async (_, _) => await install(); cancel.Click += (_, _) => updates.Cancel();
        AttachedToVisualTree += (_, _) => { updates.Changed += Refresh; Refresh(); };
        DetachedFromVisualTree += (_, _) => updates.Changed -= Refresh;
        Refresh();
    }
}
