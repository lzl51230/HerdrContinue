using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class AppStartupTests
{
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void ManualLaunchAlwaysStartsServiceWhileHiddenLaunchRequiresLoginStartup(bool enabled, bool background)
    {
        var options = LaunchOptions.Parse(background ? ["--autostart"] : []);
        Assert.Equal(background, options.IsAutoStart); Assert.Equal(!background || enabled, options.ShouldStartService(new(enabled)));
    }
    [Fact] public async Task GeneralSettingsDefaultOffAndDoNotOverwriteRegions()
    {
        var root = TempDirectory();
        try
        {
            var regions = new RegionPreferencesStore(Path.Combine(root, "settings.json"));
            var settings = new AppSettingsStore(Path.Combine(root, "app-settings.json"));
            Assert.False((await settings.LoadAsync()).Settings.AutoStartEnabled);
            await regions.SaveAsync(new(["HK", "JP"])); await settings.SaveAsync(new(true));
            Assert.Equal(new[] { "HK", "JP" }, (await regions.LoadAsync()).Preferences.PriorityRegionIds);
            await regions.SaveAsync(new(["JP"])); Assert.True((await settings.LoadAsync()).Settings.AutoStartEnabled);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory] [InlineData("{")] [InlineData("{\"version\":2,\"autoStartEnabled\":true}")] [InlineData("{\"version\":1}")]
    public async Task CorruptOrFutureSettingsSuspendLoginStartupButAllowManualLaunchWithoutOverwriting(string text)
    {
        var root = TempDirectory(); var path = Path.Combine(root, "app-settings.json");
        try
        {
            await File.WriteAllTextAsync(path, text);
            var loaded = await new AppSettingsStore(path).LoadAsync(); Assert.False(loaded.Settings.AutoStartEnabled); Assert.NotNull(loaded.Warning);
            Assert.True(LaunchOptions.Parse([]).ShouldStartService(loaded.Settings));
            Assert.False(LaunchOptions.Parse(["--autostart"]).ShouldStartService(loaded.Settings));
            Assert.Equal(text, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task MissingRegistrationSuspendsLoginStartupButAllowsManualLaunch()
    {
        var controller = new AppSettingsController(new MemorySettings { Value = new(true) }, new MemoryRegistration());
        Assert.False((await controller.InitializeAsync()).Settings.AutoStartEnabled); Assert.NotNull(controller.Current.Warning);
        Assert.True(LaunchOptions.Parse([]).ShouldStartService(controller.Current.Settings));
        Assert.False(LaunchOptions.Parse(["--autostart"]).ShouldStartService(controller.Current.Settings));
    }
    [Fact] public async Task SaveFailureRestoresTheOriginalStartupCommand()
    {
        var entry = new AutoStartEntry("old executable --autostart", true);
        var registration = new MemoryRegistration { Entry = entry };
        var controller = new AppSettingsController(new MemorySettings { Value = new(true), FailSave = true }, registration);
        await Assert.ThrowsAsync<IOException>(() => controller.ApplyAsync(new(false)));
        Assert.Equal(entry, registration.Entry); Assert.True(controller.Current.Settings.AutoStartEnabled);
    }
    [Fact] public async Task RegistrationFailureLeavesThePreferenceUntouched()
    {
        var store = new MemorySettings();
        var controller = new AppSettingsController(store, new MemoryRegistration { FailSet = true });
        await Assert.ThrowsAsync<IOException>(() => controller.ApplyAsync(new(true)));
        Assert.False(store.Value.AutoStartEnabled); Assert.Equal(0, store.Saves);
    }
    [Fact] public async Task RollbackFailureShowsActualStateAndDoesNotClaimEnabled()
    {
        var registration = new MemoryRegistration { FailRestore = true };
        var controller = new AppSettingsController(new MemorySettings { FailSave = true }, registration);
        var error = await Assert.ThrowsAsync<IOException>(() => controller.ApplyAsync(new(true)));
        Assert.Contains("未能恢复", error.Message); Assert.NotNull(controller.Current.Registration.Value);
        Assert.False(controller.Current.Settings.AutoStartEnabled);
    }
    [Fact] public async Task ConcurrentSettingsChangesFinishWithMatchingDiskAndRegistration()
    {
        var store = new MemorySettings(); var registration = new MemoryRegistration();
        var controller = new AppSettingsController(store, registration);
        await Task.WhenAll(controller.ApplyAsync(new(true)), controller.ApplyAsync(new(false)));
        Assert.False(store.Value.AutoStartEnabled); Assert.Null(registration.Entry.Value); Assert.False(controller.Current.Settings.AutoStartEnabled);
    }
    [Fact] public async Task MacRegistrationPreservesArgumentsAndDoesNotKeepAnExplicitlyQuitAppAlive()
    {
        var root = TempDirectory(); var executable = Path.Combine(root, "中文 & path", "HerdrAutoContinue");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!); await File.WriteAllTextAsync(executable, "fixture");
        var path = Path.Combine(root, "LaunchAgents", "qa.plist");
        var command = new AutoStartCommand(executable, ["--autostart", "--qa-settings-dir", root]);
        var registration = new LegacyMacAutoStartRegistration(command, path, "watchdog-qa", Path.Combine(root, "logs", "startup.log"));
        try
        {
            await registration.SetEnabledAsync(true); Assert.True((await registration.ReadAsync()).Enabled);
            var document = XDocument.Load(path); var dict = document.Root!.Element("dict")!;
            var keys = dict.Elements("key").ToDictionary(e => e.Value, e => e.NextNode as XElement);
            Assert.Equal(new[] { executable, "--autostart", "--qa-settings-dir", root }, keys["ProgramArguments"]!.Elements("string").Select(e => e.Value));
            Assert.Equal("true", keys["RunAtLoad"]!.Name.LocalName); Assert.Equal("false", keys["KeepAlive"]!.Name.LocalName);
            Assert.Equal("Aqua", keys["LimitLoadToSessionType"]!.Value);
            await registration.SetEnabledAsync(false); Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task WindowsRegistrationOnlyChangesItsOwnValueAndRoundTripsItsSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "HerdrContinue-QA-" + Guid.NewGuid().ToString("N");
        var command = new AutoStartCommand(Environment.ProcessPath!, ["--autostart"]);
        var registration = new WindowsAutoStartRegistration(command, name);
        try
        {
            await registration.SetEnabledAsync(true); var original = await registration.ReadAsync(); Assert.True(original.Enabled);
            await registration.SetEnabledAsync(false); Assert.Null((await registration.ReadAsync()).Value);
            await registration.RestoreAsync(original); Assert.Equal(original, await registration.ReadAsync());
        }
        finally { await registration.SetEnabledAsync(false); }
    }
    [Fact] public void WindowsCommandQuotesSpacesUnicodeAndTrailingBackslashes()
    {
        var command = new AutoStartCommand(@"C:\中文 path\HerdrAutoContinue.exe", ["--autostart", @"C:\directory with spaces\", "a\"b"]);
        Assert.Equal("\"C:\\中文 path\\HerdrAutoContinue.exe\" --autostart \"C:\\directory with spaces\\\\\" \"a\\\"b\"", command.WindowsCommandLine);
    }
    [Fact] public async Task WindowsRejectsOverlongStartupCommandsBeforeChangingTheRegistry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "HerdrContinue-QA-" + Guid.NewGuid().ToString("N");
        IAutoStartRegistration registration = new WindowsAutoStartRegistration(new(Environment.ProcessPath!, [new string('x', 300)]), name);
        await Assert.ThrowsAsync<HerdrException>(() => registration.SetEnabledAsync(true));
        Assert.Null((await registration.ReadAsync()).Value);
    }
    private sealed class MemorySettings : IAppSettingsStore
    {
        public AppSettings Value = new(); public bool FailSave; public int Saves;
        public Task<AppSettingsLoad> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettingsLoad(Value));
        public Task SaveAsync(AppSettings value, CancellationToken ct = default)
        { if (FailSave) throw new IOException("disk full"); Value = value; Saves++; return Task.CompletedTask; }
    }
    private sealed class MemoryRegistration : IAutoStartRegistration
    {
        public AutoStartEntry Entry = new(null, false); public bool FailSet, FailRestore;
        public string? AvailabilityError => null;
        public Task<AutoStartEntry> ReadAsync(CancellationToken ct = default) => Task.FromResult(Entry);
        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
        { if (FailSet) throw new IOException("denied"); Entry = new(enabled ? "current --autostart" : null, enabled); return Task.CompletedTask; }
        public Task RestoreAsync(AutoStartEntry entry, CancellationToken ct = default)
        { if (FailRestore) throw new IOException("rollback denied"); Entry = entry; return Task.CompletedTask; }
    }
}

public sealed class AppInstanceTests
{
    [Fact] public async Task AnotherProcessActivatesTheOnlyPrimaryAndCanRestartAfterExit()
    {
        var scope = "watchdog-qa-instance-" + Guid.NewGuid().ToString("N");
        using var first = Start(scope);
        try
        {
            Assert.Equal("primary", await Line(first));
            using var second = Start(scope);
            Assert.Equal("activated", await Line(first)); Assert.Equal("secondary", await Line(second));
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); Assert.Equal(0, second.ExitCode);
            await first.StandardInput.WriteLineAsync("exit"); await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            using var restarted = Start(scope);
            try { Assert.Equal("primary", await Line(restarted)); }
            finally { await Finish(restarted); }
        }
        finally { await Finish(first); }
    }
    [Fact] public async Task ADeadPrimaryDoesNotLeaveAStaleLockOrSocket()
    {
        var scope = "watchdog-qa-crash-" + Guid.NewGuid().ToString("N");
        using var first = Start(scope); Assert.Equal("primary", await Line(first));
        first.Kill(true); await first.WaitForExitAsync();
        using var next = Start(scope);
        try { Assert.Equal("primary", await Line(next)); }
        finally { await Finish(next); }
    }
    [Fact] public async Task ShortPipePathWorksWithLongScopesAndDifferentTemporaryDirectories()
    {
        var scope = "watchdog-qa-" + new string('x', 250) + Guid.NewGuid().ToString("N");
        var root = TempDirectory();
        using var first = Start(scope);
        try
        {
            Assert.Equal("primary", await Line(first));
            using var second = Start(scope, root);
            Assert.Equal("secondary", await Line(second)); Assert.Equal("activated", await Line(first));
            await second.WaitForExitAsync(); Assert.Equal(0, second.ExitCode);
        }
        finally { await Finish(first); Directory.Delete(root, true); }
    }
    private static Process Start(string scope, string? temporary = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "HerdrContinue.slnx"))) root = root.Parent ?? throw new IOException("Repository not found");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var binary = Path.Combine(root.FullName, "Tests", "Watchdog.FakeAgent", "bin", configuration, "net10.0", "codex.dll");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(binary); info.ArgumentList.Add("--instance-qa"); info.ArgumentList.Add(scope);
        if (temporary is not null) info.Environment[OperatingSystem.IsWindows() ? "TEMP" : "TMPDIR"] = temporary;
        return Process.Start(info)!;
    }
    private static Task<string?> Line(Process process) => process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task Finish(Process process)
    {
        if (!process.HasExited) { await process.StandardInput.WriteLineAsync("exit"); process.StandardInput.Close(); }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
    }
}
