using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watchdog.Infrastructure;

public sealed record UpdateRelease(string Version, string Url, string? Digest, string? Checksums, long Size);
public sealed record UpdateManifest(string Version, string Runtime, string[] Files);
public sealed record UpdateTransaction(string Target, string Stage, string Backup, int Parent, bool Running, string Version);

public sealed class AppUpdates : IDisposable
{
    public const string Repository = "https://api.github.com/repos/lzl51230/HerdrContinue/releases/latest";
    public static string Runtime => (OperatingSystem.IsWindows() ? "win" : "osx") + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public static string CurrentVersion => typeof(AppUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public static string Cache
    {
        get
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("WATCHDOG_QA_SETTINGS_DIR") is { } qa && Path.GetFileName(qa).StartsWith("watchdog-qa-", StringComparison.Ordinal)) return Path.Combine(qa, "Updates");
#endif
            return Path.Combine(Path.GetDirectoryName(AppPaths.AppSettingsPath)!, "Updates");
        }
    }
    public string Status { get; private set; } = "当前版本 " + CurrentVersion;
    public string? RestartMessage { get; private set; }
    public void ReportRestart(bool rolledBack) { RestartMessage = rolledBack ? "更新失败，已恢复旧版" : "更新完成 · " + CurrentVersion; Set(RestartMessage); }
    public bool Busy { get; private set; }
    public bool Installing { get; private set; }
    public UpdateRelease? Available { get; private set; }
    public event Action? Changed;
    private readonly HttpClient client;
    private CancellationTokenSource? operation;
    public AppUpdates(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HerdrAutoContinue/" + CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }
    public static SocketsHttpHandler CreateHandler() => new() {
        Proxy = new WebProxy("http://127.0.0.1:7890", false) { BypassList = [], Credentials = null },
        UseProxy = true, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(15)
    };
    public static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^v?\d+\.\d+\.\d+(?:\+[-\w.]+)?$")) throw new InvalidDataException("版本格式无法识别");
        return Version.Parse(value.TrimStart('v').Split('+')[0]);
    }
    public static UpdateRelease? SelectRelease(JsonElement root, string current, string runtime)
    {
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var version = root.GetProperty("tag_name").GetString()!;
        if (ParseVersion(version) <= ParseVersion(current)) return null;
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var asset = assets.SingleOrDefault(a => a.GetProperty("name").GetString() == $"HerdrAutoContinue-{runtime}.zip");
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("新版尚未提供当前平台安装包，请查看 GitHub Release");
        var sums = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == "SHA256SUMS.txt");
        return new(version, asset.GetProperty("browser_download_url").GetString()!, asset.TryGetProperty("digest", out var d) ? d.GetString() : null,
            sums.ValueKind == JsonValueKind.Undefined ? null : sums.GetProperty("browser_download_url").GetString(), asset.GetProperty("size").GetInt64());
    }
    private void Set(string value)
    {
        Status = value;
        if (!value.StartsWith("正在下载", StringComparison.Ordinal)) new JsonEventLog().Append(new(DateTimeOffset.Now, "更新", value));
        Changed?.Invoke();
    }
    private async Task<HttpResponseMessage> GetAsync(string address, CancellationToken ct)
    {
        var uri = new Uri(address);
        for (var hop = 0; hop < 6; hop++)
        {
            if (uri.Scheme != "https") throw new InvalidDataException("更新只允许 HTTPS");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            { uri = new Uri(uri, location); response.Dispose(); continue; }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new IOException("下载重定向次数过多");
    }
    private async Task<string> TextAsync(string address, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await GetAsync(address, deadline.Token);
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, deadline.Token);
        return await response.Content.ReadAsStringAsync(deadline.Token);
    }
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (Busy) return;
        Busy = true; Set("正在通过 127.0.0.1:7890 检查更新…");
        try { using var json = JsonDocument.Parse(await TextAsync(Repository, ct)); Available = SelectRelease(json.RootElement, CurrentVersion, Runtime); Set(Available is null ? "已是最新版本 · " + CurrentVersion : "发现新版本 " + Available.Version); }
        catch (Exception e) { Set("检查失败（代理 127.0.0.1:7890）：" + e.Message); }
        finally { Busy = false; Changed?.Invoke(); }
    }
    public async Task RunAsync(CancellationToken ct)
    {
        try { await CheckAsync(ct); using var timer = new PeriodicTimer(TimeSpan.FromHours(6)); while (await timer.WaitForNextTickAsync(ct)) await CheckAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public void Cancel() { if (!Installing) operation?.Cancel(); }
    public async Task InstallAsync(Func<bool> running, Func<Task> quit)
    {
        if (Busy || Available is not { } release) return;
        Busy = true; operation = new(); var ct = operation.Token;
        string? work = null; var helperStarted = false;
        try
        {
            var target = UpdateInstaller.InstallRoot();
            if (!File.Exists(Path.Combine(target, OperatingSystem.IsMacOS() ? "Contents/Resources/update-manifest.json" : "update-manifest.json")))
                throw new IOException("请使用正式安装包运行更新");
            work = Path.Combine(Cache, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
            var zip = Path.Combine(work, "download.zip");
            var digest = release.Digest;
            if (digest is null && release.Checksums is { } sums)
            {
                var line = (await TextAsync(sums, ct)).Split('\n').FirstOrDefault(l => l.TrimEnd().EndsWith($"HerdrAutoContinue-{Runtime}.zip", StringComparison.Ordinal));
                digest = line is null ? null : "sha256:" + line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            }
            if (digest is null || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) throw new InvalidDataException("Release 缺少有效 SHA-256 校验值");
            using (var response = await GetAsync(release.Url, ct))
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(zip))
            {
                var buffer = new byte[65536]; long total = 0;
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromSeconds(60));
                    var n = await source.ReadAsync(buffer, idle.Token); if (n == 0) break;
                    total += n; if (total > release.Size) throw new InvalidDataException("安装包大小不符");
                    await output.WriteAsync(buffer.AsMemory(0, n), ct); Set($"正在下载 {total * 100 / Math.Max(1, release.Size)}% · 代理 127.0.0.1:7890");
                }
                if (total != release.Size) throw new InvalidDataException("安装包下载不完整");
            }
            Set("正在校验安装包…");
            await using (var stream = File.OpenRead(zip))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 校验失败");
            var stage = Path.Combine(work, "stage");
            await Task.Run(() => UpdateInstaller.Extract(zip, stage), ct);
            if (OperatingSystem.IsMacOS()) stage = Path.Combine(stage, "Herdr Auto Continue.app");
            UpdateInstaller.Validate(stage, release.Version, Runtime);
            ct.ThrowIfCancellationRequested();
            var tx = Path.Combine(work, "transaction.json");
            File.WriteAllText(tx, JsonSerializer.Serialize(new UpdateTransaction(target, stage, target + ".update-backup", Environment.ProcessId, running(), release.Version)));
            var helperName = "Watchdog.Updater" + (OperatingSystem.IsWindows() ? ".exe" : "");
            var helper = Path.Combine(work, helperName); File.Copy(Path.Combine(AppContext.BaseDirectory, helperName), helper);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Installing = true; Set("正在准备安装／等待系统授权…");
            using var process = UpdateInstaller.Start(helper, "coordinate", tx); helperStarted = true;
            await UpdateInstaller.WaitFile(tx + ".ready", TimeSpan.FromMinutes(5), process, ct);
            Set("正在安装并重启…"); File.WriteAllText(tx + ".commit", ""); await quit();
        }
        catch (Exception e) { Set(e is OperationCanceledException ? "更新已取消" : "更新失败：" + e.Message); }
        finally
        {
            if (!helperStarted && work is not null) { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            Installing = false; Busy = false; operation?.Dispose(); operation = null; Changed?.Invoke();
        }
    }
    public async Task RecoverPendingAsync(Func<bool> running, Func<Task> quit)
    {
        if (!Directory.Exists(Cache) || Busy) return;
        // The coordinator owns the transaction that launched this process until it
        // consumes the health acknowledgement. Never race it by clearing its signals.
        _ = UpdateInstaller.RestartContext(out var restartingTransaction);
        foreach (var file in Directory.EnumerateFiles(Cache, "transaction.json", SearchOption.AllDirectories))
        {
            try
            {
                if (file == restartingTransaction) continue;
                var tx = UpdateInstaller.ReadTransaction(file);
                if (tx.Target != UpdateInstaller.InstallRoot() || !Directory.Exists(tx.Backup) || !File.Exists(file + ".phase") || File.ReadAllText(file + ".phase") != "replacing") continue;
                if (File.Exists(file + ".coordinator") && int.TryParse(File.ReadAllText(file + ".coordinator"), out var pid))
                { try { using var active = Process.GetProcessById(pid); if (!active.HasExited) continue; } catch (ArgumentException) { } }
                // Completion may have happened while checking the coordinator process.
                if (!Directory.Exists(tx.Backup) || File.ReadAllText(file + ".phase") != "replacing") continue;
                Busy = Installing = true; Set("检测到未完成更新，正在恢复旧版…");
                foreach (var suffix in new[] { ".ready", ".commit", ".installed", ".healthy", ".decision", ".error" }) File.Delete(file + suffix);
                File.WriteAllText(file, JsonSerializer.Serialize(tx with { Parent = Environment.ProcessId, Running = running() }));
                File.WriteAllText(file + ".recover", "");
                var helper = Path.Combine(Path.GetDirectoryName(file)!, "Watchdog.Updater" + (OperatingSystem.IsWindows() ? ".exe" : ""));
                using var process = UpdateInstaller.Start(helper, "coordinate", file);
                await UpdateInstaller.WaitFile(file + ".ready", TimeSpan.FromMinutes(5), process);
                File.WriteAllText(file + ".commit", ""); await quit(); return;
            }
            catch (Exception error) { Set("恢复失败，已保留更新备份：" + error.Message); }
            finally { Busy = Installing = false; Changed?.Invoke(); }
        }
    }
    public void Dispose() { operation?.Cancel(); client.Dispose(); }
}
