namespace Watchdog.Core;

public enum AppTheme { System, LightModern, DarkModern, LightPlus, DarkPlus, Monokai, SolarizedDark }

public sealed record AppSettings(bool AutoStartEnabled = false, AppTheme Theme = AppTheme.System);
public sealed record AppSettingsChange(AppTheme Theme, bool? AutoStartEnabled = null);

public static class AppThemes
{
    public static string Id(AppTheme theme) => theme switch
    {
        AppTheme.System => "system", AppTheme.LightModern => "light-modern", AppTheme.DarkModern => "dark-modern",
        AppTheme.LightPlus => "light-plus", AppTheme.DarkPlus => "dark-plus", AppTheme.Monokai => "monokai",
        AppTheme.SolarizedDark => "solarized-dark", _ => throw new ArgumentOutOfRangeException(nameof(theme))
    };
    public static bool TryParse(string? id, out AppTheme theme)
    {
        foreach (var candidate in Enum.GetValues<AppTheme>())
            if (Id(candidate) == id) { theme = candidate; return true; }
        theme = AppTheme.System; return false;
    }
}

public sealed record LaunchOptions(bool StartHidden = false)
{
    public static LaunchOptions Parse(IEnumerable<string> arguments) => new(arguments.Contains("--autostart", StringComparer.Ordinal));
    public bool ShouldStartService(AppSettings settings) => settings.AutoStartEnabled;
}
