using System.Text.Json;
using Watchdog.Core;

namespace Watchdog.Infrastructure;

public sealed record AppSettingsLoad(AppSettings Settings, string? Warning = null);
public interface IAppSettingsStore
{
    Task<AppSettingsLoad> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

public sealed class AppSettingsStore(string? path = null) : IAppSettingsStore
{
    private readonly string path = path ?? AppPaths.AppSettingsPath;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private sealed record Document(int Version, bool? AutoStartEnabled, JsonElement Theme = default, JsonElement SilentStartEnabled = default);
    public async Task<AppSettingsLoad> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path)) return new(new());
            if (new FileInfo(path).Length > 65536) throw new JsonException("设置文件过大");
            var document = JsonSerializer.Deserialize<Document>(await File.ReadAllTextAsync(path, ct), Json);
            if (document is not { Version: 1, AutoStartEnabled: not null }) throw new JsonException("设置格式无法识别");
            var theme = AppTheme.System;
            var validTheme = document.Theme.ValueKind == JsonValueKind.Undefined ||
                (document.Theme.ValueKind == JsonValueKind.String && AppThemes.TryParse(document.Theme.GetString(), out theme));
            var validSilent = document.SilentStartEnabled.ValueKind is JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False;
            var silent = document.SilentStartEnabled.ValueKind == JsonValueKind.True;
            var warnings = new List<string>();
            if (!validTheme) warnings.Add("颜色主题无法识别，已改为跟随系统。");
            if (!validSilent) warnings.Add("静默启动设置无法识别，已关闭静默启动。");
            return new(new(document.AutoStartEnabled.Value, theme, silent), warnings.Count == 0 ? null : string.Join(" ", warnings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return new(new(), "通用设置读取失败，登录自启动已暂停：" + e.Message); }
    }
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { Version = 1, settings.AutoStartEnabled, settings.SilentStartEnabled, Theme = AppThemes.Id(settings.Theme) }, Json), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
