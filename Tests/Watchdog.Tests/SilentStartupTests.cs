using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class SilentStartupTests
{
    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)]
    [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    [InlineData(true, true, false)] [InlineData(true, true, true)]
    public void PresentationAndServiceHaveIndependentPolicies(bool autoStart, bool silent, bool enabled)
    {
        var options = LaunchOptions.Parse(autoStart ? ["--autostart"] : []);
        var settings = new AppSettings(enabled, AppTheme.DarkPlus, silent);
        Assert.Equal(!silent, options.ShouldShowMainWindow(settings));
        Assert.Equal(!autoStart || enabled, options.ShouldStartService(settings));
    }

    [Theory]
    [InlineData("", false, false)] [InlineData(",\"silentStartEnabled\":true", true, false)]
    [InlineData(",\"silentStartEnabled\":false", false, false)]
    [InlineData(",\"silentStartEnabled\":null", false, true)]
    [InlineData(",\"silentStartEnabled\":\"true\"", false, true)]
    [InlineData(",\"silentStartEnabled\":42", false, true)]
    public async Task LegacyAndInvalidSilentSettingsPreserveOtherPreferences(string field, bool silent, bool warning)
    {
        var root = TempDirectory(); var path = Path.Combine(root, "app-settings.json");
        try
        {
            var text = "{\"version\":1,\"autoStartEnabled\":true,\"theme\":\"dark-plus\"" + field + "}";
            await File.WriteAllTextAsync(path, text);
            var loaded = await new AppSettingsStore(path).LoadAsync();
            Assert.Equal(new AppSettings(true, AppTheme.DarkPlus, silent), loaded.Settings);
            Assert.Equal(warning, loaded.Warning is not null);
            Assert.Equal(text, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SilentOnlySaveDoesNotTouchRegistrationAndOtherChangesPreserveSilent()
    {
        var root = TempDirectory();
        try
        {
            var store = new AppSettingsStore(Path.Combine(root, "app-settings.json"));
            await store.SaveAsync(new(true, AppTheme.DarkPlus));
            var registration = new CountingRegistration();
            var controller = new AppSettingsController(store, registration);
            await controller.InitializeAsync();
            await controller.ApplyChangeAsync(new(AppTheme.DarkPlus, SilentStartEnabled: true));
            Assert.Equal(new AppSettings(true, AppTheme.DarkPlus, true), (await store.LoadAsync()).Settings);
            await controller.ApplyChangeAsync(new(AppTheme.Monokai));
            Assert.Equal(new AppSettings(true, AppTheme.Monokai, true), (await store.LoadAsync()).Settings);
            await controller.ApplyAsync(new(true, AppTheme.LightPlus, false));
            Assert.False((await store.LoadAsync()).Settings.SilentStartEnabled);
            Assert.Equal(1, registration.Writes);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CountingRegistration : IAutoStartRegistration
    {
        public int Writes;
        public string? AvailabilityError => null;
        public Task<AutoStartEntry> ReadAsync(CancellationToken ct = default) =>
            Writes == 0 ? Task.FromResult(new AutoStartEntry(null, false)) : Task.FromResult(new AutoStartEntry("fixture", true));
        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) { Writes++; return Task.CompletedTask; }
        public Task RestoreAsync(AutoStartEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }
}
