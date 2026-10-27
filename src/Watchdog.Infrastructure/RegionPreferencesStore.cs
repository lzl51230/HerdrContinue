using System.Text.Json;
using Watchdog.Core;

namespace Watchdog.Infrastructure;

public sealed record RegionPreferencesLoad(ProxyRegionPreferences Preferences, string? Warning = null);
public interface IRegionPreferencesStore
{
    Task<RegionPreferencesLoad> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ProxyRegionPreferences preferences, CancellationToken cancellationToken = default);
}

public sealed class RegionPreferencesStore(string? path = null) : IRegionPreferencesStore
{
    private readonly string path = path ?? AppPaths.SettingsPath;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private sealed record Settings(int Version, string[]? PriorityRegionIds);
    public async Task<RegionPreferencesLoad> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(path)) return new(ProxyRegionPreferences.Default);
            if (new FileInfo(path).Length > 65536) throw new JsonException("设置文件过大");
            var settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(path, cancellationToken), Json);
            if (settings is not { Version: 1, PriorityRegionIds: not null }) throw new JsonException("设置格式无法识别");
            return new(new(settings.PriorityRegionIds));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(ProxyRegionPreferences.Default, "地区设置读取失败，暂用日本默认设置：" + e.Message); }
    }
    public async Task SaveAsync(ProxyRegionPreferences preferences, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path); var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Settings(1, preferences.PriorityRegionIds.ToArray()), Json), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
