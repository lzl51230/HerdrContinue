#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
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
            result["exitStoppedService"] = !model.QaSnapshot.Running;
            result["exitStoppedProxy"] = model.QaSnapshot.Proxy.Phase == ProxyPhase.Stopped;
            result["closedCountdownsCleared"] = model.QaSnapshot.Rows.All(r => r.Deadline is null);
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        };
        try
        {
            await Task.Delay(500);
            result["initialStopped"] = !model.QaSnapshot.Running;
            if ((Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO") ?? "").StartsWith("regions", StringComparison.Ordinal))
            {
                await RunRegionsAsync(window, model, directory, result); await window.ExitAsync(); return;
            }
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
            await window.ExitAsync();
        }
        catch (Exception e) { result["error"] = e.ToString(); await window.ExitAsync(); }
    }
    private static async Task WaitAsync(Func<bool> condition, int seconds = 8)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }
    private static void Capture(Window window, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
        bitmap.Render(window); bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
    private static T Find<T>(Window window, string id) where T : Control => window.GetVisualDescendants().OfType<T>()
        .Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Click(Window window, string id) => Find<Button>(window, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Select(Window window, string id, bool value) => Find<CheckBox>(window, "region-check-" + id).IsChecked = value;
    private static async Task RunRegionsAsync(MainWindow window, MainViewModel model, string directory, Dictionary<string, object?> result)
    {
        await model.InitializeAsync();
        if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO") == "regions-reload")
        {
            result["restartRestoredOrder"] = model.RegionPreferences.PriorityRegionIds.SequenceEqual(["HK", "JP", "SG"]);
            if (!(bool)result["restartRestoredOrder"]!) throw new InvalidOperationException("Saved region order was not restored");
            Capture(window, Path.Combine(directory, "reloaded.png")); return;
        }
        if (model.RegionStatus.Regions.Count != 4) throw new InvalidOperationException("Expected four QA regions");
        var cancelled = new RegionSettingsWindow(model); var cancelledTask = cancelled.ShowDialog(window); await Task.Delay(150);
        Select(cancelled, "HK", true); Select(cancelled, "SG", true); Click(cancelled, "region-up-HK");
        Capture(cancelled, Path.Combine(directory, "regions-draft.png")); Click(cancelled, "region-cancel"); await cancelledTask;
        result["cancelKeptOriginal"] = model.RegionPreferences.PriorityRegionIds.SequenceEqual(["JP"]);
        if (!(bool)result["cancelKeptOriginal"]!) throw new InvalidOperationException("Cancelled region edits became active");
        var applied = new RegionSettingsWindow(model); var appliedTask = applied.ShowDialog(window); await Task.Delay(150);
        Select(applied, "HK", true); Select(applied, "SG", true); Click(applied, "region-up-HK"); Click(applied, "region-apply"); await appliedTask;
        result["appliedOrder"] = model.RegionPreferences.PriorityRegionIds.ToArray();
        if (!model.RegionPreferences.PriorityRegionIds.SequenceEqual(["HK", "JP", "SG"])) throw new InvalidOperationException("Incorrect applied order");
        await model.ToggleAsync();
        await WaitAsync(() => model.QaSnapshot.Proxy.Phase == ProxyPhase.Healthy && model.QaSnapshot.Proxy.CurrentRegionId == "HK", 20);
        result["regionalRecoverySucceeded"] = model.QaSnapshot.Proxy.Selected == "验收香港01";
        if (!(bool)result["regionalRecoverySucceeded"]!) throw new InvalidOperationException("Wrong recovery node");
        Capture(window, Path.Combine(directory, "regions-recovered.png"));
        var reopened = new RegionSettingsWindow(model); var reopenedTask = reopened.ShowDialog(window); await Task.Delay(150);
        result["checkboxesRestored"] = new[] { "HK", "JP", "SG" }.All(id => Find<CheckBox>(reopened, "region-check-" + id).IsChecked == true);
        Capture(reopened, Path.Combine(directory, "regions-applied.png")); Click(reopened, "region-cancel"); await reopenedTask;
    }
}
#endif
