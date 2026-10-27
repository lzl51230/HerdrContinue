using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Watchdog.Infrastructure;

// FileShare.None holds an OS lock across processes, independent of TMPDIR and thread ownership.
public sealed class AppInstance : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly FileStream? lease;
    private readonly NamedPipeServerStream? server;
    private readonly Task listening = Task.CompletedTask;
    private readonly object sync = new();
    private Action? activate;
    private bool activationPending;
    private int disposed;
    public bool IsPrimary { get; }
    public string PipeName { get; }

    public AppInstance(string scope = "production")
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows()) user = user.ToUpperInvariant() + "|" + Process.GetCurrentProcess().SessionId;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + "|" + scope)))[..24];
        PipeName = OperatingSystem.IsWindows() ? "hdac-" + id : "/tmp/hdac-" + id + ".sock";
        var directory = Path.Combine(Path.GetDirectoryName(AppPaths.AppSettingsPath)!, "Instances");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var leasePath = Path.Combine(directory, id + ".lock");
        try
        {
            try { lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); IsPrimary = true; }
            catch (IOException) when (File.Exists(leasePath)) { IsPrimary = false; }
            if (IsPrimary)
            {
                server = new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                listening = ListenAsync();
            }
        }
        catch { Dispose(); throw; }
    }
    public void SetActivationHandler(Action handler)
    {
        bool pending;
        lock (sync) { activate = handler; pending = activationPending; activationPending = false; }
        if (pending) handler();
    }
    public async Task NotifyAsync(CancellationToken ct = default, bool activateWindow = true)
    {
        if (IsPrimary) throw new InvalidOperationException("Primary instance cannot notify itself");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(deadline.Token);
        var command = activateWindow ? (byte)1 : (byte)2;
        await client.WriteAsync(new[] { command }, deadline.Token);
        var reply = new byte[1];
        await client.ReadExactlyAsync(reply, deadline.Token);
        if (reply[0] != command) throw new IOException("应用唤醒未确认");
    }
    private async Task ListenAsync()
    {
        var ct = lifetime.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await server!.WaitForConnectionAsync(ct);
                using var peer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                peer.CancelAfter(TimeSpan.FromSeconds(2));
                var request = new byte[1];
                await server.ReadExactlyAsync(request, peer.Token);
                if (request[0] == 1)
                {
                    Action? handler;
                    lock (sync) { handler = activate; if (handler is null) activationPending = true; }
                    handler?.Invoke();
                }
                if (request[0] is 1 or 2) await server.WriteAsync(request, peer.Token);
            }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) break; }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }
            catch (IOException) { if (!ct.IsCancellationRequested) await Task.Delay(50, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
            finally
            {
                try { if (!ct.IsCancellationRequested && server!.IsConnected) server.Disconnect(); }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); server?.Dispose();
        try { listening.GetAwaiter().GetResult(); }
        finally { lease?.Dispose(); lifetime.Dispose(); }
    }
}
