#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Watchdog.Core;
using Watchdog.Infrastructure;

namespace HerdrAutoContinue;

internal static class StartupQaAutomation
{
    public static async Task RunAsync(DesktopApplicationController host, MainViewModel model, AppSettingsController settings,
        string directory, LaunchOptions options)
    {
        Directory.CreateDirectory(directory);
        var result = new Dictionary<string, object?> { ["pid"] = Environment.ProcessId };
        host.QaStopped += () =>
        {
            result["exitStoppedService"] = !model.QaSnapshot.Running;
            result["exitStoppedProxy"] = model.QaSnapshot.Proxy.Phase == ProxyPhase.Stopped;
            Write(directory, "result.json", result);
        };
        var scenario = Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO")!;
        try
        {
            await Task.Delay(300);
            var expected = !options.StartHidden || !scenario.StartsWith("startup-off", StringComparison.Ordinal);
            await WaitAsync(() => model.QaSnapshot.Running == expected);
            result["initialRunning"] = model.QaSnapshot.Running;
            result["trayVisible"] = host.QaTrayVisible;
            if (options.StartHidden)
            {
                Require(host.QaWindow is null, "Silent startup constructed a main window");
                result["initialHidden"] = true;
                Write(directory, "background-ready.json", result);
                await Task.Delay(3000);
            }
            else Require(host.QaWindow?.IsVisible == true, "Manual launch did not show its window");
            if (scenario.Contains("agent", StringComparison.Ordinal))
            {
                await WaitAsync(() => model.QaSnapshot.Events.Any(e => e.Message.Contains("Herdr 已确认提交")), 25);
                result["automaticContinuationConfirmed"] = true;
            }
            host.ShowMainWindow();
            var window = host.QaWindow!;
            if (scenario.StartsWith("startup-off", StringComparison.Ordinal) || scenario is "startup-corrupt" or "startup-missing-registration")
            {
                Require(!settings.Current.Settings.AutoStartEnabled && model.QaSnapshot.Running == !options.StartHidden, "Disabled login startup changed manual launch behavior");
                Require(settings.Current.Registration.Value is null, "Manual launch registered login startup");
                if (scenario is "startup-corrupt" or "startup-missing-registration")
                    Require(settings.Current.Warning is not null, "Invalid login startup did not show a warning");
                result["disabledLoginStartupRespected"] = true;
            }
            else if (scenario == "startup-enable")
            {
                Require(settings.Current.Registration.Value is null, "First launch registered login startup");
                await model.StopAsync();
                Click(window, "app-settings"); await WaitAsync(() => host.QaSettingsWindow is not null);
                var panel = host.QaSettingsWindow!;
                Find<CheckBox>(panel, "startup-enabled").IsChecked = true;
                Click(panel, "settings-cancel"); await WaitAsync(() => host.QaSettingsWindow is null);
                Require(!settings.Current.Settings.AutoStartEnabled, "Cancel enabled startup");
                Click(window, "app-settings"); await WaitAsync(() => host.QaSettingsWindow is not null);
                panel = host.QaSettingsWindow!;
                Find<CheckBox>(panel, "startup-enabled").IsChecked = true;
                Capture(panel, Path.Combine(directory, "settings-enabled.png"));
                Click(panel, "settings-apply"); await WaitAsync(() => host.QaSettingsWindow is null);
                Require(settings.Current.Settings.AutoStartEnabled, "Startup preference did not persist");
                Require(!model.QaSnapshot.Running, "Enabling startup changed the current service state");
                result["cancelKeptOriginal"] = true; result["startupEnabled"] = true;
            }
            else
            {
                window.Close(); await Task.Delay(200);
                Require(!window.IsVisible && model.QaSnapshot.Running, "Closing the window stopped monitoring");
                result["closeHidesAndKeepsService"] = true;
                host.ShowMainWindow();
                await model.StopAsync(); await WaitAsync(() => !model.QaSnapshot.Running);
                window.Close();
                await LaunchSecondaryAsync(true); await Task.Delay(200);
                Require(!window.IsVisible && !model.QaSnapshot.Running, "Repeated system startup showed or restarted the app");
                await LaunchSecondaryAsync(false); await WaitAsync(() => window.IsVisible);
                Require(!model.QaSnapshot.Running, "Reopening restarted a manually stopped service");
                result["singleInstanceActivation"] = true; result["manualStopPreserved"] = true;
                Capture(window, Path.Combine(directory, "reopened-stopped.png"));
                if (scenario == "startup-disable")
                {
                    await model.StartServiceAsync(); await WaitAsync(() => model.QaSnapshot.Running);
                    Click(window, "app-settings"); await WaitAsync(() => host.QaSettingsWindow is not null);
                    var panel = host.QaSettingsWindow!;
                    Find<CheckBox>(panel, "startup-enabled").IsChecked = false;
                    Click(panel, "settings-apply"); await WaitAsync(() => host.QaSettingsWindow is null);
                    Require(!settings.Current.Settings.AutoStartEnabled && settings.Current.Registration.Value is null, "Startup entry was not removed");
                    Require(model.QaSnapshot.Running, "Disabling startup stopped the current service");
                    result["startupDisabled"] = true;
                }
            }
            result["success"] = true;
            if (scenario == "startup-os-shutdown")
            {
                var cancelled = host.QaRequestSystemShutdown();
                result["osShutdownNotCancelled"] = !cancelled;
                Require(!cancelled, "Application cancelled operating-system shutdown");
            }
        }
        catch (Exception e) { result["success"] = false; result["error"] = e.ToString(); }
        finally
        {
            await host.QuitAsync();
            result["exitStoppedService"] = !model.QaSnapshot.Running;
            result["exitStoppedProxy"] = model.QaSnapshot.Proxy.Phase == ProxyPhase.Stopped;
            Write(directory, "result.json", result);
        }
    }
    private static void Write(string directory, string name, object value) => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task WaitAsync(Func<bool> condition, int seconds = 8)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(30, timeout.Token);
    }
    private static T Find<T>(Window window, string id) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Click(Window window, string id) => Find<Button>(window, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Capture(Window window, string path)
    {
        using var image = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
        image.Render(window); image.Save(path, PngBitmapEncoderOptions.Default);
    }
    private static async Task LaunchSecondaryAsync(bool hidden)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (hidden) info.ArgumentList.Add("--autostart");
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Require(process.ExitCode == 0, "Secondary instance failed to activate the primary");
    }
}
#endif
