using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Watchdog.Core;

namespace Watchdog.Infrastructure;

public interface ILocalConnectionFactory
{
    ValueTask<Stream> OpenAsync(LocalEndpoint endpoint, CancellationToken cancellationToken);
}

public sealed class LocalConnectionFactory : ILocalConnectionFactory
{
    public async ValueTask<Stream> OpenAsync(LocalEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (endpoint.Kind == EndpointKind.NamedPipe)
        {
            const string prefix = @"\\.\pipe\";
            var name = endpoint.Address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? endpoint.Address[prefix.Length..] : endpoint.Address;
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(cancellationToken); return pipe; }
            catch { pipe.Dispose(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), cancellationToken); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }
}

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false
    };
}

public sealed class SocketRpc(ILocalConnectionFactory? connections = null, TimeSpan? timeout = null)
{
    private readonly ILocalConnectionFactory connections = connections ?? new LocalConnectionFactory();
    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromSeconds(3);
    public const int MaximumResponseBytes = 4 * 1024 * 1024;

    public async Task<JsonElement> CallAsync(LocalEndpoint endpoint, string method,
        IReadOnlyDictionary<string, object?>? parameters = null, bool waitForWorking = false,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct = deadline.Token;
        var id = Guid.NewGuid().ToString("N");
        var args = parameters is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(parameters);
        if (waitForWorking) args["wait"] = new { until = new[] { "working" }, timeout_ms = 5000 };
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = args });
        var request = new byte[payload.Length + 1]; payload.CopyTo(request, 0); request[^1] = 10;
        var mayHaveSent = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = await connections.OpenAsync(endpoint, ct);
            ct.ThrowIfCancellationRequested();
            // Stream.WriteAsync can fail after writing a prefix. Treat every started write as ambiguous.
            mayHaveSent = true;
            await stream.WriteAsync(request, ct); await stream.FlushAsync(ct);
            using var response = new MemoryStream();
            var buffer = new byte[16384];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, ct);
                if (count == 0) throw new HerdrException("Herdr 连接已关闭", false);
                var newline = Array.IndexOf(buffer, (byte)10, 0, count);
                var length = newline >= 0 ? newline : count;
                if (response.Length + length >= MaximumResponseBytes) throw new HerdrException("Herdr 响应过大", false);
                response.Write(buffer, 0, length);
                if (newline >= 0) break;
            }
            using var document = JsonDocument.Parse(response.ToArray());
            var envelope = document.RootElement;
            if (!envelope.TryGetProperty("id", out var actualId) || actualId.ValueKind != JsonValueKind.String || actualId.GetString() != id)
                throw new HerdrException("Herdr 响应无法验证", false);
            if (envelope.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.ToString() : "error";
                var message = error.TryGetProperty("message", out var m) ? m.ToString() : "请求失败";
                throw new HerdrException($"{code}：{message}", new[] { "agent_blocked", "agent_not_found", "agent_not_ready", "invalid_params", "method_not_found" }.Contains(code));
            }
            if (!envelope.TryGetProperty("result", out var result)) throw new HerdrException("Herdr 缺少响应结果", false);
            return result.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException e) { throw new HerdrException("Herdr 请求超时", !mayHaveSent, e); }
        catch (HerdrException) { throw; }
        catch (Exception e) when (e is IOException or SocketException or JsonException or InvalidOperationException or ArgumentException)
        { throw new HerdrException("Herdr 通信失败：" + e.Message, !mayHaveSent, e); }
    }
}
