using Watchdog.Core;

namespace Watchdog.Infrastructure;

public sealed class RegionSettingsController(IRegionPreferencesStore store, ProxyMonitor proxy)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string?> InitializeAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            var loaded = await store.LoadAsync(ct);
            await proxy.RefreshCatalogueAsync(ct);
            var preferences = loaded.Preferences;
            if (loaded.Warning is null && preferences.SelectedSourceIds is null && proxy.Status.CurrentSourceId is { } id)
            { preferences = new(preferences.PriorityRegionIds, [id]); await store.SaveAsync(preferences, ct); }
            await proxy.ApplyPreferencesAsync(preferences, ct); return loaded.Warning;
        }
        finally { gate.Release(); }
    }
    public async Task ApplyAsync(ProxyRegionPreferences preferences, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await proxy.ConfigureAsync(preferences, () => store.SaveAsync(preferences, ct), ct);
        }
        finally { gate.Release(); }
    }
    public async Task RestoreAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            var id = proxy.Status.CurrentSourceId ?? throw new IOException("未找到当前基础订阅");
            var value = new ProxyRegionPreferences(proxy.Preferences.PriorityRegionIds, [id]);
            await proxy.ConfigureAsync(value, () => store.SaveAsync(value, ct), ct, restore: true);
            await proxy.RefreshCatalogueAsync(ct);
        }
        finally { gate.Release(); }
    }

}
