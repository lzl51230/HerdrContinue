using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Watchdog.Core;

namespace HerdrAutoContinue;

public sealed record ThemeChoice(AppTheme Theme, string Label)
{
    public override string ToString() => Label;
}

public interface ISystemThemeSource : IDisposable
{
    bool IsDark { get; }
    event EventHandler? Changed;
}

// VS Code workbench colors, adapted to application controls. See docs/theme-colors.md.
public sealed class AppThemeManager : IDisposable
{
    public static IReadOnlyList<ThemeChoice> Choices { get; } = new[]
    {
        new ThemeChoice(AppTheme.System, "跟随系统"), new(AppTheme.LightModern, "Light Modern（现代浅色）"),
        new(AppTheme.DarkModern, "Dark Modern（现代深色）"), new(AppTheme.LightPlus, "Light+（经典浅色）"),
        new(AppTheme.DarkPlus, "Dark+（经典深色）"), new(AppTheme.Monokai, "Monokai"), new(AppTheme.SolarizedDark, "Solarized Dark")
    };
    private readonly Application app;
    private readonly ISystemThemeSource system;
    public AppTheme Saved { get; private set; } = AppTheme.System;
    public AppTheme Current { get; private set; } = AppTheme.System;
    public AppTheme Effective { get; private set; }
    public AppThemeManager(Application app, ISystemThemeSource? system = null)
    {
        this.app = app; this.system = system ?? new PlatformThemeSource(app.PlatformSettings);
        this.system.Changed += SystemColorsChanged;
        Apply(AppTheme.System);
    }
    public void Commit(AppTheme theme) { Saved = theme; Apply(theme); }
    public void Preview(AppTheme theme) => Apply(theme);
    public void CancelPreview() => Apply(Saved);
    private void SystemColorsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    { if (Current == AppTheme.System) Apply(Current); });
    private void Apply(AppTheme theme)
    {
        Current = theme;
        Effective = theme == AppTheme.System
            ? (system.IsDark ? AppTheme.DarkModern : AppTheme.LightModern) : theme;
        var p = Palette.For(Effective);
        app.Resources = CreateResources(p);
        app.RequestedThemeVariant = p.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
    public void Dispose() { system.Changed -= SystemColorsChanged; system.Dispose(); }
    private sealed class PlatformThemeSource : ISystemThemeSource
    {
        private readonly IPlatformSettings? platform;
        public PlatformThemeSource(IPlatformSettings? platform)
        { this.platform = platform; if (platform is not null) platform.ColorValuesChanged += OnChanged; }
        public bool IsDark => platform?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
        public event EventHandler? Changed;
        private void OnChanged(object? sender, PlatformColorValues e) => Changed?.Invoke(this, EventArgs.Empty);
        public void Dispose() { if (platform is not null) platform.ColorValuesChanged -= OnChanged; }
    }
    public static void Bind(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, new DynamicResourceExtension(key));
    private static ResourceDictionary CreateResources(Palette p)
    {
        var r = new ResourceDictionary();
        void Brush(string key, string value) => r[key] = SolidColorBrush.Parse(value);
        foreach (var (key, value) in new[]
        {
            ("AppBackground", p.Background), ("AppForeground", p.Foreground), ("AppSecondary", p.Muted),
            ("AppPanel", p.Panel), ("AppSummary", p.Hover), ("AppBorder", p.Border),
            ("AppAccent", p.Link), ("AppWarning", p.Warning), ("AppError", p.Error)
        }) Brush(key, value);
        // Fluent templates resolve these resources dynamically, including their interaction states.
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            var disabled = state == "Disabled";
            var surface = state is "PointerOver" or "Pressed" ? p.Hover : p.Input;
            foreach (var prefix in new[] { "Button", "ComboBox" })
            {
                Brush(prefix + "Background" + state, surface);
                Brush(prefix + "Foreground" + state, disabled ? p.Muted : p.Foreground);
                Brush(prefix + "BorderBrush" + state, state == "Pressed" ? p.Link : p.Border);
            }
            Brush("AccentButtonBackground" + state, disabled ? p.Input : state == "" ? p.Button : p.ButtonHover);
            Brush("AccentButtonForeground" + state, disabled ? p.Muted : p.ButtonText);
            Brush("AccentButtonBorderBrush" + state, disabled ? p.Border : p.Button);
            foreach (var selection in new[] { "", "Selected" })
            {
                var selected = selection == "Selected";
                Brush("ComboBoxItemBackground" + selection + state, selected ? p.Selection : state == "" ? p.Panel : p.Hover);
                Brush("ComboBoxItemForeground" + selection + state, disabled ? p.Muted : selected ? p.SelectionText : p.Foreground);
                Brush("ComboBoxItemBorderBrush" + selection + state, "Transparent");
            }
            foreach (var selection in new[] { "Unchecked", "Checked", "Indeterminate" })
            {
                var selected = selection != "Unchecked";
                Brush("CheckBoxForeground" + selection + state, disabled ? p.Muted : p.Foreground);
                Brush("CheckBoxBackground" + selection + state, "Transparent");
                Brush("CheckBoxBorderBrush" + selection + state, "Transparent");
                Brush("CheckBoxCheckBackgroundFill" + selection + state, selected && !disabled ? p.Button : surface);
                Brush("CheckBoxCheckBackgroundStroke" + selection + state, selected && !disabled ? p.Button : p.Muted);
                Brush("CheckBoxCheckGlyphForeground" + selection + state, disabled ? p.Muted : selected ? p.ButtonText : "Transparent");
            }
        }
        foreach (var key in new[] { "ComboBoxDropDownBackground", "ToolTipBackground" }) Brush(key, p.Panel);
        foreach (var key in new[] { "ComboBoxDropDownBorderBrush", "ToolTipBorderBrush", "ComboBoxBackgroundBorderBrushUnfocused" }) Brush(key, p.Border);
        foreach (var key in new[] { "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed", "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed", "ToolTipForeground" }) Brush(key, p.Foreground);
        Brush("ComboBoxBackgroundUnfocused", p.Input);
        Brush("ComboBoxDropDownGlyphForegroundDisabled", p.Muted);
        Brush("ComboBoxBackgroundBorderBrushFocused", p.Link);
        Brush("SystemControlFocusVisualPrimaryBrush", p.Link);
        Brush("SystemControlFocusVisualSecondaryBrush", p.Background);
        Brush("SystemControlErrorTextForegroundBrush", p.Error);
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorDark1" }) r[key] = Color.Parse(p.Button);
        return r;
    }
    private sealed record Palette(bool Dark, string Background, string Panel, string Foreground, string Muted, string Border,
        string Input, string Hover, string Button, string ButtonHover, string ButtonText, string Link,
        string Selection, string SelectionText, string Warning, string Error)
    {
        public static Palette For(AppTheme theme) => theme switch
        {
            AppTheme.LightModern => new(false, "#FFFFFF", "#F8F8F8", "#3B3B3B", "#616161", "#CECECE", "#FFFFFF", "#E5E5E5", "#005FB8", "#0258A8", "#FFFFFF", "#005FB8", "#E8E8E8", "#000000", "#895503", "#B42318"),
            AppTheme.DarkModern => new(true, "#1F1F1F", "#181818", "#CCCCCC", "#9D9D9D", "#3C3C3C", "#313131", "#2B2B2B", "#0078D4", "#026EC1", "#FFFFFF", "#75BEFF", "#04395E", "#FFFFFF", "#E2C08D", "#F85149"),
            AppTheme.LightPlus => new(false, "#FFFFFF", "#F3F3F3", "#333333", "#616161", "#CECECE", "#FFFFFF", "#E8E8E8", "#0070C1", "#005A9E", "#FFFFFF", "#006AB1", "#0060C0", "#FFFFFF", "#795E26", "#A1260D"),
            AppTheme.DarkPlus => new(true, "#1E1E1E", "#252526", "#D4D4D4", "#A6A6A6", "#454545", "#3C3C3C", "#37373D", "#0E639C", "#1177BB", "#FFFFFF", "#4DAAFC", "#094771", "#FFFFFF", "#DCDCAA", "#F48771"),
            AppTheme.Monokai => new(true, "#272822", "#1E1F1C", "#F8F8F2", "#B0B0A5", "#535548", "#34352F", "#414339", "#75715E", "#625F4F", "#FFFFFF", "#A6E22E", "#57594D", "#F8F8F2", "#E6DB74", "#FF6188"),
            AppTheme.SolarizedDark => new(true, "#002B36", "#00212B", "#93A1A1", "#839496", "#31545C", "#003847", "#073642", "#086B9D", "#075C88", "#FFFFFF", "#42A5DB", "#004454", "#FDF6E3", "#C9A932", "#F27869"),
            _ => throw new ArgumentOutOfRangeException(nameof(theme))
        };
    }
}
