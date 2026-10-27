namespace Watchdog.Core;

public sealed record AppSettings(bool AutoStartEnabled = false);

public sealed record LaunchOptions(bool StartHidden = false)
{
    public static LaunchOptions Parse(IEnumerable<string> arguments) => new(arguments.Contains("--autostart", StringComparer.Ordinal));
    public bool ShouldStartService(AppSettings settings) => settings.AutoStartEnabled;
}
