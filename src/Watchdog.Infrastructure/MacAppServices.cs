using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Watchdog.Infrastructure;

public interface IMacLoginItemService
{
    AutoStartState Status { get; }
    void SetEnabled(bool enabled);
    void OpenSettings();
    void PromptForApproval();
}

[SupportedOSPlatform("macos")]
public sealed class MacAppServices : IMacLoginItemService
{
    private const string Library = "HerdrMacIntegration";
    [DllImport(Library, EntryPoint = "hdac_login_status")] private static extern int StatusNative();
    [DllImport(Library, EntryPoint = "hdac_login_set")] private static extern int SetNative(int enabled, byte[] error, int capacity);
    [DllImport(Library, EntryPoint = "hdac_login_open_settings")] private static extern void OpenNative();
    [DllImport(Library, EntryPoint = "hdac_login_prompt")] private static extern void PromptNative();
#if DEBUG
    [DllImport(Library, EntryPoint = "hdac_login_qa_prompt")] public static extern void QaPrompt(int settings);
#endif
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void LaunchCallback(int autoStart);
    [DllImport(Library, EntryPoint = "hdac_launch_install")] public static extern void InstallLaunchHandler(LaunchCallback callback, int autoStart);
    [DllImport(Library, EntryPoint = "hdac_launch_reveal")] public static extern void Reveal();
    [DllImport(Library, EntryPoint = "hdac_launch_restore")] public static extern void RestoreLaunchHandler();
    public AutoStartState Status => StatusNative() switch
    {
        0 => AutoStartState.NotRegistered, 1 => AutoStartState.Enabled, 2 => AutoStartState.RequiresApproval,
        3 => AutoStartState.NotFound, _ => AutoStartState.Unavailable
    };
    public void SetEnabled(bool enabled)
    {
        var error = new byte[2048];
        if (SetNative(enabled ? 1 : 0, error, error.Length) != 0)
            throw new IOException("macOS 登录项登记失败：" + Encoding.UTF8.GetString(error).TrimEnd('\0'));
    }
    public void OpenSettings() => OpenNative();
    public void PromptForApproval() => PromptNative();
}
