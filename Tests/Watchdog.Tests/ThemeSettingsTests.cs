using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class ThemeSettingsTests
{
    [Theory]
    [InlineData(AppTheme.System)] [InlineData(AppTheme.LightModern)] [InlineData(AppTheme.DarkModern)]
    [InlineData(AppTheme.LightPlus)] [InlineData(AppTheme.DarkPlus)] [InlineData(AppTheme.Monokai)] [InlineData(AppTheme.SolarizedDark)]
    public async Task ThemesRoundTripWithoutLosingStartup(AppTheme theme)
    {
        var root = TempDirectory();
        try
        {
            var store = new AppSettingsStore(Path.Combine(root, "app-settings.json"));
            await store.SaveAsync(new(true, theme));
            var loaded = await store.LoadAsync();
            Assert.Equal(new AppSettings(true, theme), loaded.Settings); Assert.Null(loaded.Warning);
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("")] [InlineData(",\"theme\":\"future-theme\"")] [InlineData(",\"theme\":42")]
    [InlineData(",\"theme\":null")] [InlineData(",\"theme\":{}")]
    public async Task LegacyAndUnknownThemesDefaultToSystemWithoutDisablingStartup(string field)
    {
        var root = TempDirectory(); var path = Path.Combine(root, "app-settings.json");
        try
        {
            var json = "{\"version\":1,\"autoStartEnabled\":true" + field + "}";
            await File.WriteAllTextAsync(path, json);
            var loaded = await new AppSettingsStore(path).LoadAsync();
            Assert.True(loaded.Settings.AutoStartEnabled); Assert.Equal(AppTheme.System, loaded.Settings.Theme);
            Assert.Equal(field.Length > 0, loaded.Warning is not null);
            Assert.Equal(json, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ThemeOnlySaveDoesNotWriteOrRequireReadingTheStartupEntry(bool denyRead)
    {
        var store = new MemoryStore { Value = new(true) };
        var registration = new Registration { DenyRead = denyRead };
        var controller = new AppSettingsController(store, registration);
        await controller.InitializeAsync();
        Assert.False(controller.Current.Settings.AutoStartEnabled);
        await controller.ApplyChangeAsync(new(AppTheme.Monokai));
        Assert.Equal(new AppSettings(true, AppTheme.Monokai), store.Value);
        Assert.Equal(AppTheme.Monokai, controller.Current.Settings.Theme); Assert.Equal(0, registration.Writes);
    }
    [Fact] public async Task ThemeSaveFailurePreservesTheSavedThemeAndStartupEntry()
    {
        var store = new MemoryStore { Value = new(true, AppTheme.DarkPlus), Fail = true };
        var registration = new Registration(); var controller = new AppSettingsController(store, registration);
        await controller.InitializeAsync();
        await Assert.ThrowsAsync<IOException>(() => controller.ApplyChangeAsync(new(AppTheme.LightModern)));
        Assert.Equal(AppTheme.DarkPlus, controller.Current.Settings.Theme);
        Assert.Equal(new AppSettings(true, AppTheme.DarkPlus), store.Value); Assert.Equal(0, registration.Writes);
    }
    [Fact] public async Task CombinedSaveFailureRollsBackRegistrationAndKeepsSavedTheme()
    {
        var store = new MemoryStore { Value = new(false, AppTheme.DarkPlus), Fail = true };
        var registration = new Registration(); var controller = new AppSettingsController(store, registration);
        await Assert.ThrowsAsync<IOException>(() => controller.ApplyChangeAsync(new(AppTheme.LightModern, true)));
        Assert.Equal(AppTheme.DarkPlus, controller.Current.Settings.Theme); Assert.Null(registration.Entry.Value);
        Assert.Equal(2, registration.Writes);
    }
    [Fact] public async Task ConcurrentPartialChangesPreserveTheOtherPreference()
    {
        var store = new MemoryStore(); var registration = new Registration();
        var controller = new AppSettingsController(store, registration);
        await Task.WhenAll(controller.ApplyChangeAsync(new(AppTheme.DarkModern, true)), controller.ApplyChangeAsync(new(AppTheme.SolarizedDark)));
        Assert.Equal(new AppSettings(true, AppTheme.SolarizedDark), store.Value);
        Assert.True(controller.Current.Settings.AutoStartEnabled); Assert.Equal(1, registration.Writes);
    }
    [Fact] public async Task CooldownPartialChangePreservesStartupAndOtherPreferences()
    {
        var store = new MemoryStore { Value = new(true, AppTheme.Monokai, true, 10) };
        var registration = new Registration { DenyRead = true };
        var controller = new AppSettingsController(store, registration);
        await controller.ApplyChangeAsync(new(AppTheme.Monokai, CooldownSeconds: 1));
        await controller.ApplyChangeAsync(new(AppTheme.DarkPlus));
        Assert.Equal(new AppSettings(true, AppTheme.DarkPlus, true, 1), store.Value);
        Assert.Equal(0, registration.Writes);
    }
    [Fact] public async Task CooldownSaveFailureAndInvalidRangePreserveSavedValue()
    {
        var store = new MemoryStore { Value = new(CooldownSeconds: 10), Fail = true };
        var controller = new AppSettingsController(store, new Registration());
        await controller.InitializeAsync();
        await Assert.ThrowsAsync<IOException>(() => controller.ApplyChangeAsync(new(AppTheme.System, CooldownSeconds: 5)));
        Assert.Equal(10, controller.Current.Settings.CooldownSeconds);
        foreach (var seconds in new[] { 0, 301 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.ApplyChangeAsync(new(AppTheme.System, CooldownSeconds: seconds)));
        Assert.Equal(10, store.Value.CooldownSeconds);
    }
    [Fact] public async Task FeatureChangesPreserveOtherSettingsAndFailureLeavesFlagsUntouched()
    {
        var store = new MemoryStore { Value = new(true, AppTheme.Monokai, true, 10) };
        var registration = new Registration { DenyRead = true };
        var controller = new AppSettingsController(store, registration);
        await controller.ApplyChangeAsync(new(AppTheme.Monokai, ProxyRecoveryEnabled: false));
        await controller.ApplyChangeAsync(new(AppTheme.DarkPlus, AgentRecoveryEnabled: false));
        Assert.Equal(new AppSettings(true,AppTheme.DarkPlus,true,10,false,false),store.Value);
        Assert.Equal(0,registration.Writes);store.Fail=true;
        await Assert.ThrowsAsync<IOException>(()=>controller.ApplyChangeAsync(new(AppTheme.DarkPlus,ProxyRecoveryEnabled:true)));
        Assert.False(controller.Current.Settings.ProxyRecoveryEnabled);Assert.False(controller.Current.Settings.AgentRecoveryEnabled);
    }
    private sealed class MemoryStore : IAppSettingsStore
    {
        public AppSettings Value = new(); public bool Fail;
        public Task<AppSettingsLoad> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettingsLoad(Value));
        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        { if (Fail) throw new IOException("disk full"); Value = settings; return Task.CompletedTask; }
    }
    private sealed class Registration : IAutoStartRegistration
    {
        public bool DenyRead; public int Writes; public AutoStartEntry Entry = new(null, false);
        public string? AvailabilityError => null;
        public Task<AutoStartEntry> ReadAsync(CancellationToken ct = default) => DenyRead ? throw new UnauthorizedAccessException("denied") : Task.FromResult(Entry);
        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
        { Writes++; Entry = new(enabled ? "fixture --autostart" : null, enabled); return Task.CompletedTask; }
        public Task RestoreAsync(AutoStartEntry entry, CancellationToken ct = default) { Writes++; Entry = entry; return Task.CompletedTask; }
    }
}
