namespace Watchdog.Core;

public enum AppTheme { System, LightModern, DarkModern, LightPlus, DarkPlus, Monokai, SolarizedDark }

public sealed record AppSettings(bool AutoStartEnabled = false, AppTheme Theme = AppTheme.System, bool SilentStartEnabled = false, int CooldownSeconds = 5);
public sealed record AppSettingsChange(AppTheme Theme, bool? AutoStartEnabled = null, bool? SilentStartEnabled = null, int? CooldownSeconds = null);

public static class InterventionCooldown
{
    public static void Validate(int seconds)
    {
        if (seconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(seconds), "冷却时长应为 1～300 秒整数");
    }
}

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

public sealed record LaunchOptions(bool IsAutoStart = false)
{
    public static LaunchOptions Parse(IEnumerable<string> arguments) => new(arguments.Contains("--autostart", StringComparer.Ordinal));
    public bool ShouldShowMainWindow(AppSettings settings) => !settings.SilentStartEnabled;
    public bool ShouldStartService(AppSettings settings) => !IsAutoStart || settings.AutoStartEnabled;
}
