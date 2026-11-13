#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using System.Net;
using System.Text.Json;
using Watchdog.Infrastructure;
namespace HerdrAutoContinue;
internal static class UpdateQaAutomation
{
    internal sealed class ReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var package = Environment.GetEnvironmentVariable("WATCHDOG_QA_UPDATE_PACKAGE");
            if (package is not null && request.RequestUri!.AbsoluteUri != AppUpdates.Repository)
            {
                if (request.RequestUri.Host == "github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/qa-package.zip") } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(package)) });
            }
            var version = package is null ? "v99.0.0" : "v1.6.2";
            var hash = package is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
            if (Environment.GetEnvironmentVariable("WATCHDOG_QA_UPDATE_BEHAVIOR") == "corrupt") hash = new string('0', 64);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = version, assets = new[] { new { name = $"HerdrAutoContinue-{AppUpdates.Runtime}.zip", browser_download_url = $"https://github.com/lzl51230/HerdrContinue/releases/download/{version}/test.zip", size = package is null ? 100 : new FileInfo(package).Length, digest = hash is null ? null : "sha256:" + hash } } })) });
        }
    }
    public static async Task RunEndToEndAsync(DesktopApplicationController host, MainViewModel model, AppUpdates updates, string directory)
    {
        Directory.CreateDirectory(directory);
        var running = Environment.GetEnvironmentVariable("WATCHDOG_QA_UPDATE_RUNNING") == "true";
        var behavior = Environment.GetEnvironmentVariable("WATCHDOG_QA_UPDATE_BEHAVIOR") ?? "success";
        var result = new Dictionary<string, object?>();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var restart = UpdateInstaller.RestartContext(out var transaction);
            if (restart is not null)
            {
                var expected = behavior == "startup-failure" ? "1.6.1" : "1.6.2";
                for (var attempt = 0; attempt < 50 && model.Running != running; attempt++) await Task.Delay(100, deadline.Token);
                if (AppUpdates.CurrentVersion != expected || model.Running != running) throw new Exception($"Restart mismatch: version={AppUpdates.CurrentVersion}, expected={expected}; running={model.Running}, expected={running}");
                while (File.ReadAllText(transaction! + ".phase") is not ("complete" or "rolled-back")) await Task.Delay(100, deadline.Token);
                result["phase"] = File.ReadAllText(transaction! + ".phase");
                result["restarted"] = true;
                if (OperatingSystem.IsWindows()) result["administrator"] = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            else
            {
                if (!running) { await model.StopAsync(); while (model.Running) await Task.Delay(30, deadline.Token); }
                await updates.CheckAsync();
                if (updates.Available is null) throw new Exception("Missing release: " + updates.Status);
                host.ShowMainWindow(); await Task.Delay(100, deadline.Token);
                File.WriteAllText(Path.Combine(directory, "clicked.json"), JsonSerializer.Serialize(new { version = AppUpdates.CurrentVersion, model.Running }));
                var button = host.QaWindow!.GetVisualDescendants().OfType<Button>().Single(c => Avalonia.Automation.AutomationProperties.GetAutomationId(c) == "update-install");
                button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                while (updates.Busy && !host.IsExiting) await Task.Delay(100, deadline.Token);
                if (host.IsExiting) return; // The replacement process writes the final result.
                if (behavior != "corrupt" && behavior != "cancel-authorization") throw new Exception(updates.Status);
                if (behavior == "corrupt" && !updates.Status.Contains("SHA-256")) throw new Exception("Wrong integrity failure: " + updates.Status);
                if (model.Running != running || AppUpdates.CurrentVersion != "1.6.1") throw new Exception("Failure changed original application");
                result["failurePreservedApplication"] = true; result["status"] = updates.Status;
            }
            host.ShowMainWindow(); await Task.Delay(150);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)host.QaWindow!.ClientSize.Width, (int)host.QaWindow.ClientSize.Height));
            bitmap.Render(host.QaWindow); bitmap.Save(Path.Combine(directory, "result.png"), PngBitmapEncoderOptions.Default);
            result["version"] = AppUpdates.CurrentVersion; result["running"] = model.Running; result["success"] = true;
        }
        catch (Exception e) { result["success"] = false; result["error"] = e.ToString(); }
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        if (!host.IsExiting) await host.QuitAsync();
    }
    public static async Task RunAsync(DesktopApplicationController host, AppUpdates updates, string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            await updates.CheckAsync(); host.ShowMainWindow();
            await Task.Delay(300);
            Capture(host.QaWindow!, "main.png");
            var dialog = host.ShowSettingsAsync();
            while (host.QaSettingsWindow is null) await Task.Delay(30);
            var settings = host.QaSettingsWindow;
            settings.GetVisualDescendants().OfType<UpdatePanel>().Single().BringIntoView();
            await Task.Delay(300); Capture(settings, "settings.png");
            if (updates.Available?.Version != "v99.0.0") throw new Exception("New version missing");
            settings.Close(); await dialog;
            File.WriteAllText(Path.Combine(directory, "result.json"), "{\"success\":true,\"sharedUpdateUi\":true}");
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { success = false, error = error.ToString() })); }
        finally { await host.QuitAsync(); }
        void Capture(Window window, string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
            bitmap.Render(window); bitmap.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
        }
    }
}
#endif
