#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Watchdog.Core;

namespace HerdrAutoContinue;

// A real-window lifecycle check for macOS without requiring accessibility permission.
// Windows QA drives the same window through UI Automation instead.
internal static class QaAutomation
{
    public static async Task RunAsync(MainWindow window, MainViewModel model, string directory)
    {
        Directory.CreateDirectory(directory);
        var result = new Dictionary<string, object?>();
        window.Closed += (_, _) =>
        {
            result["windowCloseStoppedService"] = !model.QaSnapshot.Running;
            result["windowCloseStoppedProxy"] = model.QaSnapshot.Proxy.Phase == ProxyPhase.Stopped;
            result["closedCountdownsCleared"] = model.QaSnapshot.Rows.All(r => r.Deadline is null);
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        };
        try
        {
            await Task.Delay(500);
            result["initialStopped"] = !model.QaSnapshot.Running;
            Capture(window, Path.Combine(directory, "stopped.png"));
            await model.ToggleAsync();
            await WaitAsync(() => model.QaSnapshot.Rows.Any(r => r.Deadline is not null));
            result["cooldownVisible"] = model.QaSnapshot.Rows.Any(r => r.Deadline is not null);
            Capture(window, Path.Combine(directory, "cooldown.png"));
            if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO") != "cancel")
            {
                await WaitAsync(() => model.QaSnapshot.Events.Any(e => e.Message.Contains("Herdr 已确认提交")), 20);
                result["confirmedSubmission"] = true;
                await Task.Delay(2500);
                Capture(window, Path.Combine(directory, "resumed.png"));
                await model.ToggleAsync();
                result["stopClearedCountdowns"] = !model.QaSnapshot.Running && model.QaSnapshot.Rows.All(r => r.Deadline is null);
                await model.ToggleAsync(); await Task.Delay(500);
            }
            window.Close();
        }
        catch (Exception e) { result["error"] = e.ToString(); window.Close(); }
    }
    private static async Task WaitAsync(Func<bool> condition, int seconds = 8)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }
    private static void Capture(MainWindow window, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
        bitmap.Render(window); bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
#endif
