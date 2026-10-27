using System.Diagnostics;
using System.Text;
using Watchdog.Core;

namespace Watchdog.Infrastructure;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public sealed class ProcessRunner
{
    private static readonly string[] HerdrEnvironment = ["HERDR_SESSION", "HERDR_SOCKET_PATH", "HERDR_PANE_ID", "HERDR_WORKSPACE_ID", "HERDR_TAB_ID"];
    public async Task<ProcessResult> RunAsync(string binary, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default, bool clearHerdrEnvironment = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct = deadline.Token;
        var info = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (clearHerdrEnvironment) foreach (var name in HerdrEnvironment) info.Environment.Remove(name);
        using var process = new Process { StartInfo = info };
        ct.ThrowIfCancellationRequested();
        process.Start(); process.StandardInput.Close();
        using var registration = ct.Register(() => Kill(process));
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, ct);
            var error = ReadBoundedAsync(process.StandardError.BaseStream, ct);
            await Task.WhenAll(output, error, process.WaitForExitAsync(ct));
            return new(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        { throw new HerdrException("进程执行超时", true, e); }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
    private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > SocketRpc.MaximumResponseBytes) throw new HerdrException("进程输出过大");
            output.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray()).TrimStart('\uFEFF');
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

public static class HerdrBinary
{
    public static string Resolve(string? configured = null)
    {
        configured ??= Environment.GetEnvironmentVariable("HERDR_BINARY");
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? Path.GetFullPath(configured) : throw new HerdrException("未找到 Herdr：" + configured);
        var name = OperatingSystem.IsWindows() ? "herdr.exe" : "herdr";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", name);
        if (File.Exists(fallback)) return fallback;
        throw new HerdrException("未找到 Herdr；请安装 Herdr 或通过 HERDR_BINARY 指定可执行文件");
    }
}
