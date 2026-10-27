using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Watchdog.Core;

namespace Watchdog.Infrastructure;

public static class AppPaths
{
    public static string SettingsPath => OperatingSystem.IsWindows()
        ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HerdrAutoContinue", "settings.json")
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "HerdrAutoContinue", "settings.json");
    public static string LogPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HerdrAutoContinue", "Logs", "events.jsonl")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "HerdrAutoContinue", "events.jsonl");

    public static void RevealLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : "/usr/bin/open") { UseShellExecute = false };
        if (OperatingSystem.IsWindows()) info.ArgumentList.Add(File.Exists(path) ? "/select," + path : Path.GetDirectoryName(path)!);
        else { if (File.Exists(path)) info.ArgumentList.Add("-R"); info.ArgumentList.Add(File.Exists(path) ? path : Path.GetDirectoryName(path)!); }
        using var process = Process.Start(info);
    }
}

public sealed class JsonEventLog(string? path = null) : IEventSink
{
    private readonly object sync = new();
    public string Path { get; } = path ?? AppPaths.LogPath;
    public string? LastError { get; private set; }
    public void Append(MonitorEvent value)
    {
        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 2_000_000) File.Move(Path, Path + ".previous", true);
                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { time = value.Date.ToString("O"), target = value.Target, message = value.Message }) + "\n");
                stream.Write(bytes); LastError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastError = e.Message; }
        }
    }
}
