using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using Watchdog.Core;

namespace Watchdog.Infrastructure;

public sealed record AutoStartEntry(object? Value, bool Enabled, RegistryValueKind? Kind = null);
public interface IAutoStartRegistration
{
    string? AvailabilityError { get; }
    Task<AutoStartEntry> ReadAsync(CancellationToken ct = default);
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
    Task RestoreAsync(AutoStartEntry entry, CancellationToken ct = default);
}

public sealed record AutoStartCommand(string Executable, IReadOnlyList<string> Arguments, string? AvailabilityError = null)
{
    public static AutoStartCommand ForCurrentProcess(IReadOnlyList<string>? extraArguments = null)
    {
        var executable = Environment.ProcessPath ?? "";
        string? error = null;
        if (!File.Exists(executable) || !Path.GetFileNameWithoutExtension(executable).Equals("HerdrAutoContinue", StringComparison.Ordinal))
            error = "请使用发布版应用开启自启动。";
        else if (OperatingSystem.IsMacOS() && (!executable.EndsWith(".app/Contents/MacOS/HerdrAutoContinue", StringComparison.Ordinal)
            || executable.Contains("/AppTranslocation/", StringComparison.Ordinal)))
            error = "请先将应用放入“应用程序”目录，再开启自启动。";
        return new(executable, ["--autostart", .. extraArguments ?? []], error);
    }
    public string WindowsCommandLine => string.Join(" ", new[] { Quote(Executable, true) }.Concat(Arguments.Select(a => Quote(a))));
    public static string Quote(string value, bool force = false)
    {
        if (!force && value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public void Validate()
    {
        if (AvailabilityError is not null) throw new HerdrException(AvailabilityError);
        if (!Path.IsPathFullyQualified(Executable) || !File.Exists(Executable)) throw new HerdrException("应用路径无效，请重新安装后开启自启动。");
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsAutoStartRegistration(AutoStartCommand command, string valueName = "HerdrAutoContinue") : IAutoStartRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string? AvailabilityError => command.AvailabilityError ?? (command.WindowsCommandLine.Length > 260 ? "应用路径过长，请移到更短的固定路径后开启自启动。" : null);
    public Task<AutoStartEntry> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return Task.FromResult(new AutoStartEntry(value, value is string text && text == command.WindowsCommandLine,
            value is null ? null : key!.GetValueKind(valueName)));
    }
    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (enabled)
        {
            command.Validate();
            if (AvailabilityError is not null) throw new HerdrException(AvailabilityError);
        }
        return RestoreAsync(new(enabled ? command.WindowsCommandLine : null, enabled, RegistryValueKind.String), ct);
    }
    public Task RestoreAsync(AutoStartEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (entry.Value is null) key.DeleteValue(valueName, false);
        else key.SetValue(valueName, entry.Value, entry.Kind ?? RegistryValueKind.String);
        return Task.CompletedTask;
    }
}

public sealed class MacAutoStartRegistration(AutoStartCommand command, string? path = null,
    string label = "local.alex.herdr-auto-continue", string? logPath = null) : IAutoStartRegistration
{
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", label + ".plist");
    private readonly string logPath = logPath ?? Path.Combine(Path.GetDirectoryName(AppPaths.LogPath)!, "startup.log");
    public string? AvailabilityError => command.AvailabilityError;
    public async Task<AutoStartEntry> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new(null, false);
        if (new FileInfo(path).Length > 65536) throw new IOException("系统自启动项过大，无法读取。");
        var value = await File.ReadAllTextAsync(path, ct);
        return new(value, value == CreatePlist(command, label, logPath));
    }
    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (enabled)
        {
            command.Validate();
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        }
        return RestoreAsync(new(enabled ? CreatePlist(command, label, logPath) : null, enabled), ct);
    }
    public async Task RestoreAsync(AutoStartEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (entry.Value is null) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, (string)entry.Value, new UTF8Encoding(false), ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string CreatePlist(AutoStartCommand command, string label, string logPath) => new XDocument(
        new XDeclaration("1.0", "UTF-8", null), new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
        new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
            new XElement("key", "Label"), new XElement("string", label),
            new XElement("key", "ProgramArguments"), new XElement("array", new[] { command.Executable }.Concat(command.Arguments).Select(a => new XElement("string", a))),
            new XElement("key", "RunAtLoad"), new XElement("true"),
            new XElement("key", "KeepAlive"), new XElement("false"),
            new XElement("key", "LimitLoadToSessionType"), new XElement("string", "Aqua"),
            new XElement("key", "StandardOutPath"), new XElement("string", logPath),
            new XElement("key", "StandardErrorPath"), new XElement("string", logPath)))).ToString();
}
