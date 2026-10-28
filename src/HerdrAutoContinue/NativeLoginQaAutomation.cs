#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Watchdog.Infrastructure;
using Watchdog.Core;

namespace HerdrAutoContinue;

internal static class NativeLoginQaAutomation
{
    public static async Task RunAsync(DesktopApplicationController host, MainViewModel model, AppSettingsController settings, string directory, LaunchOptions options)
    {
        Directory.CreateDirectory(directory);
        var result = new Dictionary<string, object?> { ["pid"] = Environment.ProcessId, ["hidden"] = settings.Current.Settings.SilentStartEnabled };
        var scene = Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO");
        try
        {
            if (scene == "native-register")
            {
                settings.OpenSettings(); Write("before-register-ready.json");
                await Wait(() => File.Exists(Path.Combine(directory, "begin-register")), 60);
                var window = host.QaWindow!;
                Find<Button>(window, "app-settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(() => host.QaSettingsWindow is not null);
                var panel = host.QaSettingsWindow!;
                Find<CheckBox>(panel, "startup-enabled").IsChecked = true;
                Find<CheckBox>(panel, "silent-start-enabled").IsChecked = true;
                Find<Button>(panel, "settings-apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(() => settings.Current.RequestedSettings.AutoStartEnabled);
                Require(settings.Current.Registration.Status == AutoStartState.Enabled, "Native registration did not become enabled: " + settings.Current.Warning);
                result["state"] = settings.Current.Registration.Status.ToString();
                settings.OpenSettings();
                Write("registered-ready.json");
                await Wait(() => File.Exists(Path.Combine(directory, "release")), 90);
            }
            else if (scene == "native-unregister")
            {
                var running = model.QaSnapshot.Running;
                await settings.ApplyChangeAsync(new(settings.Current.Settings.Theme, false));
                Require(settings.Current.Registration.Status == AutoStartState.NotRegistered && running == model.QaSnapshot.Running, "Unregister changed the running service or failed");
                result["state"] = settings.Current.Registration.Status.ToString();
                settings.OpenSettings(); Write("unregistered-ready.json");
                await Wait(() => File.Exists(Path.Combine(directory, "release")), 90);
            }
            else if (scene == "native-prompt")
            {
                Write("prompt-ready.json");
                if (OperatingSystem.IsMacOS()) MacAppServices.QaPrompt(directory.EndsWith("prompt-settings", StringComparison.Ordinal) ? 1 : 0);
                result["nativePromptDismissed"] = true;
                await Task.Delay(300); // Allow native modal observers to finish before the app exits.
            }
            else
            {
                Require(options.IsAutoStart && host.QaWindow is null && model.QaSnapshot.Running, "Native login was not silent or did not start the service");
                Write("background-ready.json"); await Task.Delay(3000);
                host.ShowMainWindow();
                Require(host.QaWindow?.IsVisible == true, "Manual reopen did not show the window");
                using var image = new RenderTargetBitmap(new PixelSize(1080, 850));
                image.Render(host.QaWindow!); image.Save(Path.Combine(directory, "reopened.png"), PngBitmapEncoderOptions.Default);
                await model.StopAsync();
                host.QaWindow!.Close(); Write("stopped-ready.json");
                await Wait(() => File.Exists(Path.Combine(directory, "repeat-login")), 45);
                await Task.Delay(500);
                Require(!host.QaWindow.IsVisible && !model.QaSnapshot.Running, "Repeated native login showed or restarted the app");
                Write("repeat-confirmed.json");
                await Wait(() => File.Exists(Path.Combine(directory, "manual-open")), 45);
                await Wait(() => host.QaWindow.IsVisible);
                Require(!model.QaSnapshot.Running, "Manual native reopen restarted the service");
                result["manualStopPreserved"] = true;
            }
            result["success"] = true;
        }
        catch (Exception e) { result["success"] = false; result["error"] = e.ToString(); }
        finally
        {
            host.QaStopped += () => Write("result.json");
            await host.QuitAsync();
        }
        void Write(string name) => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static T Find<T>(Window window, string id) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> value, int seconds = 10)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)); while (!value()) await Task.Delay(50, timeout.Token); }
}
#endif
