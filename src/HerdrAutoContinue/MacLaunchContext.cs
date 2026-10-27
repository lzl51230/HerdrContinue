using Watchdog.Core;
using Watchdog.Infrastructure;

namespace HerdrAutoContinue;

internal static class MacLaunchContext
{
    private static readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly MacAppServices.LaunchCallback callback = hidden => ready.TrySetResult(hidden != 0);
    private static bool installed;
    public static void Install(LaunchOptions options)
    {
        if (!OperatingSystem.IsMacOS()) return;
        MacAppServices.InstallLaunchHandler(callback, options.StartHidden ? 1 : 0); installed = true;
    }
    public static async Task<LaunchOptions> ResolveAsync(LaunchOptions options, CancellationToken ct) =>
        OperatingSystem.IsMacOS() ? options with { StartHidden = await ready.Task.WaitAsync(ct) } : options;
    public static void Reveal() { if (OperatingSystem.IsMacOS() && installed) MacAppServices.Reveal(); }
    public static void Restore() { if (OperatingSystem.IsMacOS() && installed) { MacAppServices.RestoreLaunchHandler(); installed = false; } }
}
