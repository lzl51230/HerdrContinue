using Watchdog.Core;

namespace Watchdog.Infrastructure;

public sealed class RegionSettingsController(IRegionPreferencesStore store, ProxyMonitor proxy)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<string?> InitializeAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { var loaded = await store.LoadAsync(ct); await proxy.ApplyPreferencesAsync(loaded.Preferences, ct); return loaded.Warning; }
        finally { gate.Release(); }
    }
    public async Task ApplyAsync(ProxyRegionPreferences preferences, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await store.SaveAsync(preferences, ct);
            // Once the file is committed, keep the live policy consistent even if the dialog closes.
            await proxy.ApplyPreferencesAsync(preferences);
        }
        finally { gate.Release(); }
    }
}
