using Watchdog.Core;

namespace Watchdog.Infrastructure;

public sealed record AppSettingsStatus(AppSettings Settings, AutoStartEntry Registration, string? Warning = null);
public sealed class AppSettingsController(IAppSettingsStore store, IAutoStartRegistration registration)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public AppSettingsStatus Current { get; private set; } = new(new(), new(null, false));
    public string? AvailabilityError => registration.AvailabilityError;
    public async Task<AppSettingsStatus> InitializeAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await ReloadAsync(ct); }
        finally { gate.Release(); }
    }
    private async Task<AppSettingsStatus> ReloadAsync(CancellationToken ct)
    {
        var loaded = await store.LoadAsync(ct);
        try
        {
            var entry = await registration.ReadAsync(ct);
            var warning = loaded.Warning;
            if (loaded.Settings.AutoStartEnabled && !entry.Enabled) warning ??= "系统自启动项不存在或应用位置已变化，请重新开启自启动。";
            if (!loaded.Settings.AutoStartEnabled && entry.Value is not null) warning ??= "存在未启用的系统启动项，可应用关闭设置将其移除。";
            return Current = new(new(loaded.Settings.AutoStartEnabled && entry.Enabled), entry, warning);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Current = new(new(), new(null, false), "无法读取系统自启动项，自动运行已暂停：" + e.Message); }
    }
    public async Task<AppSettingsStatus> ApplyAsync(AppSettings settings, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var previous = await registration.ReadAsync(ct);
            try
            {
                await registration.SetEnabledAsync(settings.AutoStartEnabled, ct);
                await store.SaveAsync(settings, ct);
            }
            catch (Exception failure)
            {
                try { await registration.RestoreAsync(previous); }
                catch (Exception rollback)
                {
                    await ReloadAsync(CancellationToken.None);
                    throw new IOException("保存失败，系统启动项也未能恢复；请检查当前状态并重试。", new AggregateException(failure, rollback));
                }
                await ReloadAsync(CancellationToken.None);
                throw;
            }
            return await ReloadAsync(CancellationToken.None);
        }
        finally { gate.Release(); }
    }
}
