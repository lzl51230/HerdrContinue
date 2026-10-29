using Watchdog.Core;

namespace Watchdog.Infrastructure;

public sealed record AppSettingsStatus(AppSettings Settings, AutoStartEntry Registration, string? Warning = null)
{
    public AppSettings RequestedSettings { get; init; } = Settings;
}
public sealed class AppSettingsController(IAppSettingsStore store, IAutoStartRegistration registration)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public AppSettingsStatus Current { get; private set; } = new(new(), new(null, false));
    public string? AvailabilityError => registration.AvailabilityError;
    public bool CanOpenSettings => registration.CanOpenSettings;
    public void OpenSettings() => registration.OpenSettings();
    public void PromptForApproval() => registration.PromptForApproval();
    public async Task<AppSettingsStatus> InitializeAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            string? migrationWarning = null;
            try { await registration.MigrateLegacyAsync((await store.LoadAsync(ct)).Settings.AutoStartEnabled, ct); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { migrationWarning = "旧启动项迁移失败：" + e.Message; }
            var status = await ReloadAsync(ct);
            return Current = migrationWarning is null ? status : status with { Warning = migrationWarning };
        }
        finally { gate.Release(); }
    }
    private async Task<AppSettingsStatus> ReloadAsync(CancellationToken ct)
    {
        var loaded = await store.LoadAsync(ct);
        try
        {
            var entry = await registration.ReadAsync(ct);
            var warning = loaded.Warning;
            if (entry.Status == AutoStartState.RequiresApproval) warning ??= "登录项已登记，等待系统许可；请在系统登录项中允许此应用。";
            else if (loaded.Settings.AutoStartEnabled && !entry.Enabled) warning ??= "系统自启动项已关闭、不可用或应用位置已变化，请检查后重新开启。";
            if (!loaded.Settings.AutoStartEnabled && entry.Value is not null && entry.Status is not (AutoStartState.NotFound or AutoStartState.Unavailable))
                warning ??= "系统仍有启动登记，应用未开启自动运行；请在系统启动项中检查或移除。";
            return Current = new(loaded.Settings with { AutoStartEnabled = loaded.Settings.AutoStartEnabled && entry.Enabled }, entry, warning) { RequestedSettings = loaded.Settings };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Current = new(loaded.Settings with { AutoStartEnabled = false }, new(null, false, State: AutoStartState.Unavailable), "无法读取系统自启动项，登录自启动已暂停：" + e.Message) { RequestedSettings = loaded.Settings }; }
    }
    public Task<AppSettingsStatus> ApplyAsync(AppSettings settings, CancellationToken ct = default) =>
        ApplyChangeAsync(new(settings.Theme, settings.AutoStartEnabled, settings.SilentStartEnabled, settings.CooldownSeconds, settings.ProxyRecoveryEnabled, settings.AgentRecoveryEnabled), ct);
    public async Task<AppSettingsStatus> ApplyChangeAsync(AppSettingsChange change, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            _ = AppThemes.Id(change.Theme);
            var loaded = await store.LoadAsync(ct);
            var settings = loaded.Settings with { Theme = change.Theme, AutoStartEnabled = change.AutoStartEnabled ?? loaded.Settings.AutoStartEnabled, SilentStartEnabled = change.SilentStartEnabled ?? loaded.Settings.SilentStartEnabled, CooldownSeconds = change.CooldownSeconds ?? loaded.Settings.CooldownSeconds, ProxyRecoveryEnabled = change.ProxyRecoveryEnabled ?? loaded.Settings.ProxyRecoveryEnabled, AgentRecoveryEnabled = change.AgentRecoveryEnabled ?? loaded.Settings.AgentRecoveryEnabled };
            InterventionCooldown.Validate(settings.CooldownSeconds);
            if (change.AutoStartEnabled is null)
            {
                await store.SaveAsync(settings, ct);
                return await ReloadAsync(CancellationToken.None);
            }
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
