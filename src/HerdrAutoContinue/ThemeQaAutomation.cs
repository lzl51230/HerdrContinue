#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Presenters;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Watchdog.Core;
using Watchdog.Infrastructure;

namespace HerdrAutoContinue;

internal static class ThemeQaAutomation
{
    internal static readonly TestPlatformSettings Platform = new();
    public static async Task RunAsync(DesktopApplicationController host, MainViewModel model, AppSettingsController settings,
        AppThemeManager themes, string directory, LaunchOptions options)
    {
        Directory.CreateDirectory(directory);
        var report = new Dictionary<string, object?>();
        try
        {
            var scenario = Environment.GetEnvironmentVariable("WATCHDOG_QA_SCENARIO");
            if (scenario == "theme-restart")
            {
                Require(themes.Saved == AppTheme.DarkModern && themes.Effective == AppTheme.DarkModern, "Saved startup theme was not loaded");
                if (options.StartHidden) Require(host.QaWindow is null, "Silent startup constructed a window");
                host.ShowMainWindow();
                await Task.Delay(200); CheckWindow(host.QaWindow!);
                Capture(host.QaWindow!, Path.Combine(directory, "restored.png"));
                report["restoredBeforeShowing"] = true;
            }
            else
            {
                var window = host.QaWindow!;
                var originalEntry = settings.Current.Registration;
                await model.StartServiceAsync();
                foreach (var choice in AppThemeManager.Choices.Where(c => c.Theme != AppTheme.System))
                {
                    var panel = await OpenSettings(host, window);
                    Find<ComboBox>(panel, "color-theme").SelectedItem = choice;
                    await Task.Delay(150);
                    Require(themes.Current == choice.Theme && model.QaSnapshot.Running, "Preview changed service state");
                    CheckWindow(window); CheckWindow(panel);
                    CheckContrast();
                    var name = AppThemes.Id(choice.Theme);
                    Capture(window, Path.Combine(directory, name + "-main.png"));
                    Capture(panel, Path.Combine(directory, name + "-settings.png"));
                    Find<ComboBox>(panel, "color-theme").IsDropDownOpen = true;
                    await Task.Delay(100);
                    var popup = Find<ComboBox>(panel, "color-theme").GetVisualDescendants().OfType<Popup>().Single();
                    Require(popup.Child?.IsVisible == true, "Theme dropdown did not open");
                    CaptureControl(popup.Child!, Path.Combine(directory, name + "-dropdown.png"));
                    Find<ComboBox>(panel, "color-theme").IsDropDownOpen = false;
                    var apply = Find<Button>(panel, "settings-apply");
                    var pseudo = (IPseudoClasses)apply.Classes;
                    pseudo.Set(":pointerover", true);
                    await Task.Delay(50);
                    var presenter = apply.GetVisualDescendants().OfType<ContentPresenter>().Single(c => c.Name == "PART_ContentPresenter");
                    Require(presenter.Background is ISolidColorBrush hover && hover.Color == Resource("AccentButtonBackgroundPointerOver"), "Button hover did not use the theme");
                    Capture(panel, Path.Combine(directory, name + "-hover.png"));
                    pseudo.Set(":pointerover", false); apply.IsEnabled = false;
                    await Task.Delay(50); Capture(panel, Path.Combine(directory, name + "-disabled.png"));
                    apply.IsEnabled = true;
                    panel.Width = 480; panel.Height = 350;
                    Find<ComboBox>(panel, "color-theme").Focus();
                    await Task.Delay(100); Capture(panel, Path.Combine(directory, name + "-minimum.png"));
                    Click(panel, "settings-apply"); await Wait(() => host.QaSettingsWindow is null);
                    Require(themes.Saved == choice.Theme && settings.Current.Settings.Theme == choice.Theme, "Theme did not persist");
                    Require(settings.Current.Registration == originalEntry, "Theme selection changed startup registration");
                    var regions = new RegionSettingsWindow(model); _ = regions.ShowDialog(window);
                    await Task.Delay(200); CheckWindow(regions); Capture(regions, Path.Combine(directory, name + "-regions.png")); regions.Close();
                    var delays = window.ShowDelays(); await Task.Delay(150);
                    CheckWindow(delays); Capture(delays, Path.Combine(directory, name + "-delays.png")); delays.Close();
                }
                report["sixThemesAllWindows"] = true;
                report["dropdownHoverDisabledFocusAndContrast"] = true;
                // Exercise the real platform-notification handler without changing the user's OS appearance.
                await SaveTheme(AppTheme.System);
                Platform.Set(PlatformThemeVariant.Dark); await Wait(() => themes.Effective == AppTheme.DarkModern);
                Platform.Set(PlatformThemeVariant.Light); await Wait(() => themes.Effective == AppTheme.LightModern);
                await SaveTheme(AppTheme.DarkPlus);
                Platform.Set(PlatformThemeVariant.Dark); await Task.Delay(100);
                Require(themes.Effective == AppTheme.DarkPlus, "Fixed theme followed system");
                await SaveTheme(AppTheme.System);
                var preview = await OpenSettings(host, window);
                Select(preview, AppTheme.Monokai);
                Platform.Set(PlatformThemeVariant.Light); await Task.Delay(100);
                Click(preview, "settings-cancel"); await Wait(() => host.QaSettingsWindow is null);
                Require(themes.Current == AppTheme.System && themes.Effective == AppTheme.LightModern, "Cancel did not restore the current system theme");
                preview = await OpenSettings(host, window); Select(preview, AppTheme.SolarizedDark); preview.Close();
                await Wait(() => host.QaSettingsWindow is null);
                Require(themes.Current == AppTheme.System, "Window close kept an unsaved preview");
                report["systemNotificationsAndCancel"] = true;
                await SaveTheme(AppTheme.DarkModern);
                preview = await OpenSettings(host, window); Select(preview, AppTheme.LightModern);
                var path = Path.Combine(Path.GetDirectoryName(model.LogPath)!, "app-settings.json");
                File.Move(path, path + ".qa-backup"); Directory.CreateDirectory(path);
                try
                {
                    Click(preview, "settings-apply");
                    await Wait(() => Find<TextBlock>(preview, "app-settings-message").Text?.StartsWith("保存失败") == true);
                    Require(themes.Current == AppTheme.DarkModern && themes.Saved == AppTheme.DarkModern, "Save failure kept the preview");
                    Capture(preview, Path.Combine(directory, "save-failed.png"));
                }
                finally { Directory.Delete(path); File.Move(path + ".qa-backup", path); }
                Click(preview, "settings-cancel"); await Wait(() => host.QaSettingsWindow is null);
                report["saveFailureRestoresTheme"] = true;
                report["serviceAndStartupUnchanged"] = model.QaSnapshot.Running && settings.Current.Registration == originalEntry;
                async Task SaveTheme(AppTheme theme)
                {
                    var panel = await OpenSettings(host, window); Select(panel, theme);
                    Click(panel, "settings-apply"); await Wait(() => host.QaSettingsWindow is null);
                    Require(themes.Saved == theme, "Theme save failed");
                }
            }
            report["success"] = true;
        }
        catch (Exception e) { report["success"] = false; report["error"] = e.ToString(); }
        finally
        {
            host.QaStopped += () => File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            await host.QuitAsync();
        }
    }
    private static void Select(Window panel, AppTheme theme) => Find<ComboBox>(panel, "color-theme").SelectedItem = AppThemeManager.Choices.Single(c => c.Theme == theme);
    private static async Task<AppSettingsWindow> OpenSettings(DesktopApplicationController host, Window window)
    {
        Click(window, "app-settings"); await Wait(() => host.QaSettingsWindow is not null); return host.QaSettingsWindow!;
    }
    private static void CheckWindow(Window window)
    {
        var resource = (ISolidColorBrush)Application.Current!.Resources["AppBackground"]!;
        Require(window.Background is ISolidColorBrush brush && brush.Color == resource.Color, "Window did not update its background");
        var foreground = (ISolidColorBrush)Application.Current.Resources["AppForeground"]!;
        Require(window.Foreground is ISolidColorBrush text && text.Color == foreground.Color, "Window did not update its foreground");
    }
    private static Color Resource(string key) => ((ISolidColorBrush)Application.Current!.Resources[key]!).Color;
    private static void CheckContrast()
    {
        foreach (var (text, background) in new[] { ("AppForeground", "AppBackground"), ("AppSecondary", "AppBackground"),
            ("AppWarning", "AppBackground"), ("AppError", "AppBackground"), ("AppSecondary", "AppPanel"),
            ("AppAccent", "AppSummary"), ("ButtonForeground", "ButtonBackground"),
            ("AccentButtonForeground", "AccentButtonBackground"), ("AccentButtonForegroundPointerOver", "AccentButtonBackgroundPointerOver"),
            ("ComboBoxItemForegroundSelected", "ComboBoxItemBackgroundSelected") })
        {
            var a = Luminance(Resource(text)); var b = Luminance(Resource(background));
            Require((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5, "Low text contrast: " + text + " / " + background);
        }
        static double Luminance(Color c)
        {
            static double Linear(byte v) { var x = v / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
    }
    private static T Find<T>(Window window, string id) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Click(Window window, string id) => Find<Button>(window, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!condition()) await Task.Delay(30, timeout.Token); }
    private static void Capture(Window window, string path)
    {
        using var image = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
        image.Render(window); image.Save(path, PngBitmapEncoderOptions.Default);
    }
    private static void CaptureControl(Control control, string path)
    {
        using var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(control.Bounds.Width), (int)Math.Ceiling(control.Bounds.Height)));
        image.Render(control); image.Save(path, PngBitmapEncoderOptions.Default);
    }
    internal sealed class TestPlatformSettings : ISystemThemeSource
    {
        public bool IsDark { get; private set; }
        public event EventHandler? Changed;
        public void Set(PlatformThemeVariant theme) { IsDark = theme == PlatformThemeVariant.Dark; Changed?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { }
    }
}
#endif
