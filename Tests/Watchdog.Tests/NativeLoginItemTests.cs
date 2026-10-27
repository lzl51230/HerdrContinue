using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class NativeLoginItemTests
{
    [Theory]
    [InlineData(AutoStartState.NotRegistered, false)] [InlineData(AutoStartState.Enabled, true)]
    [InlineData(AutoStartState.RequiresApproval, false)] [InlineData(AutoStartState.NotFound, false)] [InlineData(AutoStartState.Unavailable, false)]
    public async Task OnlySystemEnabledStatusAllowsAutomaticRunning(AutoStartState state, bool active)
    {
        var service = new Service { Status = state }; var store = new Store { Value = new(true, AppTheme.DarkPlus) };
        var controller = new AppSettingsController(store, Registration(service));
        var result = await controller.InitializeAsync();
        Assert.Equal(state, result.Registration.Status); Assert.Equal(active, result.Settings.AutoStartEnabled);
        Assert.True(result.RequestedSettings.AutoStartEnabled); Assert.Equal(AppTheme.DarkPlus, result.Settings.Theme);
        Assert.Equal(0, service.Writes); Assert.Equal(0, service.Prompts);
    }
    [Fact] public async Task ApprovalPendingIsSavedAsIntentWithoutClaimingTheServiceIsEnabled()
    {
        var service = new Service { Pending = true }; var store = new Store();
        var controller = new AppSettingsController(store, Registration(service));
        var result = await controller.ApplyChangeAsync(new(AppTheme.Monokai, true));
        Assert.True(store.Value.AutoStartEnabled); Assert.True(result.RequestedSettings.AutoStartEnabled);
        Assert.False(result.Settings.AutoStartEnabled); Assert.Equal(AutoStartState.RequiresApproval, result.Registration.Status);
        Assert.Contains("等待", result.Warning!); Assert.Equal(0, service.Prompts);
        controller.PromptForApproval(); Assert.Equal(1, service.Prompts);
    }
    [Fact] public async Task ExternalDisableIsRespectedOnRefreshAndThemeSave()
    {
        var service = new Service { Status = AutoStartState.Enabled }; var store = new Store { Value = new(true) };
        var controller = new AppSettingsController(store, Registration(service));
        Assert.True((await controller.InitializeAsync()).Settings.AutoStartEnabled);
        service.Status = AutoStartState.RequiresApproval;
        Assert.False((await controller.InitializeAsync()).Settings.AutoStartEnabled);
        await controller.ApplyChangeAsync(new(AppTheme.SolarizedDark));
        Assert.Equal(0, service.Writes); Assert.Equal(0, service.Prompts); Assert.True(store.Value.AutoStartEnabled);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LegacyMigrationRemovesTheOldLaunchPathEvenWhenApprovalIsPending(bool pending)
    {
        var root = TempDirectory(); var path = Path.Combine(root, "old.plist");
        try
        {
            var command = Command();
            await File.WriteAllTextAsync(path, LegacyMacAutoStartRegistration.CreatePlist(command, "watchdog-qa", Path.Combine(root, "startup.log")));
            var service = new Service { Pending = pending };
            var registration = new MacLoginItemRegistration(command, service, path, "watchdog-qa");
            var controller = new AppSettingsController(new Store { Value = new(true) }, registration);
            await controller.InitializeAsync(); Assert.False(File.Exists(path)); Assert.Equal(1, service.Writes);
            service.Status = AutoStartState.NotRegistered; // User removes the native item afterwards.
            await controller.InitializeAsync(); Assert.Equal(1, service.Writes); Assert.Equal(0, service.Prompts);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task FailedMigrationRetainsTheLegacyFileAndReportsFailure()
    {
        var root = TempDirectory(); var path = Path.Combine(root, "old.plist");
        try
        {
            var command = Command(); var original = LegacyMacAutoStartRegistration.CreatePlist(command, "watchdog-qa", Path.Combine(root, "startup.log"));
            await File.WriteAllTextAsync(path, original);
            var service = new Service { FailEnable = true };
            var controller = new AppSettingsController(new Store { Value = new(true) }, new MacLoginItemRegistration(command, service, path, "watchdog-qa"));
            Assert.Contains("迁移失败", (await controller.InitializeAsync()).Warning!);
            Assert.Equal(original, await File.ReadAllTextAsync(path)); Assert.Equal(AutoStartState.NotRegistered, service.Status);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task ForeignLegacyFileIsNeverDeletedOrMigrated()
    {
        var root = TempDirectory(); var path = Path.Combine(root, "old.plist");
        try
        {
            var text = LegacyMacAutoStartRegistration.CreatePlist(new(Command().Executable, ["--some-other-service"]), "watchdog-qa", Path.Combine(root, "startup.log"));
            await File.WriteAllTextAsync(path, text); var service = new Service();
            var registration = new MacLoginItemRegistration(Command(), service, path, "watchdog-qa");
            await Assert.ThrowsAsync<IOException>(() => registration.MigrateLegacyAsync(true));
            Assert.Equal(0, service.Writes); Assert.Equal(text, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task PreferenceSaveFailureUnregistersANewNativeItem()
    {
        var service = new Service(); var store = new Store { FailSave = true };
        var controller = new AppSettingsController(store, Registration(service));
        await Assert.ThrowsAsync<IOException>(() => controller.ApplyChangeAsync(new(AppTheme.DarkModern, true)));
        Assert.Equal(AutoStartState.NotRegistered, service.Status); Assert.False(store.Value.AutoStartEnabled);
    }
    [Fact] public async Task DisabledPreferenceDoesNotMigrateAnOldItem()
    {
        var root = TempDirectory(); var path = Path.Combine(root, "old.plist");
        try
        {
            await File.WriteAllTextAsync(path, "untouched"); var service = new Service();
            await new MacLoginItemRegistration(Command(), service, path).MigrateLegacyAsync(false);
            Assert.Equal("untouched", await File.ReadAllTextAsync(path)); Assert.Equal(0, service.Writes);
        }
        finally { Directory.Delete(root, true); }
    }
    private static AutoStartCommand Command() => new(Environment.ProcessPath!, ["--autostart"]);
    private static MacLoginItemRegistration Registration(Service service) => new(Command(), service, Path.Combine(Path.GetTempPath(), "watchdog-qa-missing-" + Guid.NewGuid() + ".plist"));
    private sealed class Service : IMacLoginItemService
    {
        public AutoStartState Status { get; set; } = AutoStartState.NotRegistered;
        public bool Pending, FailEnable; public int Writes, Prompts;
        public void SetEnabled(bool enabled)
        {
            Writes++; if (enabled && FailEnable) throw new IOException("native registration failed");
            Status = enabled ? Pending ? AutoStartState.RequiresApproval : AutoStartState.Enabled : AutoStartState.NotRegistered;
        }
        public void OpenSettings() { }
        public void PromptForApproval() => Prompts++;
    }
    private sealed class Store : IAppSettingsStore
    {
        public AppSettings Value = new(); public bool FailSave;
        public Task<AppSettingsLoad> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettingsLoad(Value));
        public Task SaveAsync(AppSettings value, CancellationToken ct = default) { if (FailSave) throw new IOException("disk full"); Value = value; return Task.CompletedTask; }
    }
}
