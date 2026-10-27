using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Watchdog.Core;

namespace Watchdog.Tests;

internal sealed class LocalFixture : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Socket? listener;
    private readonly Task acceptLoop;
    private readonly ConcurrentBag<Task> handlers = [];
    private readonly Func<Stream, CancellationToken, Task> handle;
    public LocalEndpoint Endpoint { get; }
    public LocalFixture(EndpointKind kind, Func<Stream, CancellationToken, Task> handle)
    {
        this.handle = handle;
        var name = "watchdog-qa-" + Guid.NewGuid().ToString("N")[..12];
        Endpoint = new(kind, kind == EndpointKind.UnixSocket ? Path.Combine(OperatingSystem.IsMacOS() ? "/tmp" : Path.GetTempPath(), name) : name);
        if (kind == EndpointKind.UnixSocket)
        { listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); listener.Bind(new UnixDomainSocketEndPoint(Endpoint.Address)); listener.Listen(32); }
        acceptLoop = Task.Run(AcceptAsync);
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Stream stream;
                if (listener is not null) stream = new NetworkStream(await listener.AcceptAsync(lifetime.Token), true);
                else
                {
                    var pipe = new NamedPipeServerStream(Endpoint.Address, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    try { await pipe.WaitForConnectionAsync(lifetime.Token); stream = pipe; }
                    catch { pipe.Dispose(); throw; }
                }
                handlers.Add(HandleAsync(stream));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
    }
    private async Task HandleAsync(Stream stream)
    {
        await using (stream)
        {
            try { await handle(stream, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) { /* The client may cancel or close a test connection. */ }
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener?.Dispose(); await acceptLoop; await Task.WhenAll(handlers);
        if (Endpoint.Kind == EndpointKind.UnixSocket) File.Delete(Endpoint.Address);
        lifetime.Dispose();
    }
    public static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        using var data = new MemoryStream(); var b = new byte[1];
        while (await stream.ReadAsync(b, ct) > 0) { if (b[0] == 10) break; data.WriteByte(b[0]); }
        return Encoding.UTF8.GetString(data.ToArray()).TrimEnd('\r');
    }
    public static async Task ReplyAsync(Stream stream, JsonElement request, object result, CancellationToken ct)
    { await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = request.GetProperty("id").GetString(), result }) + "\n"), ct); }
}

internal sealed record HttpRequest(string Method, string Resource, string Body);
internal sealed record HttpReply(int Status, string Body, bool Chunked = false);
internal sealed class HttpFixture : IAsyncDisposable
{
    private readonly LocalFixture fixture;
    public LocalEndpoint Endpoint => fixture.Endpoint;
    public ConcurrentQueue<HttpRequest> Requests { get; } = [];
    public HttpFixture(EndpointKind kind, Func<HttpRequest, HttpReply> respond)
    {
        fixture = new(kind, async (stream, ct) =>
        {
            var first = (await LocalFixture.ReadLineAsync(stream, ct)).Split(' ');
            var length = 0;
            while (true)
            {
                var line = await LocalFixture.ReadLineAsync(stream, ct); if (line.Length == 0) break;
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
            }
            var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
            var request = new HttpRequest(first[0], first[1], Encoding.UTF8.GetString(bytes)); Requests.Enqueue(request);
            var reply = respond(request); var body = Encoding.UTF8.GetBytes(reply.Body);
            var framing = reply.Chunked ? "Transfer-Encoding: chunked" : "Content-Length: " + body.Length;
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Status} OK\r\nContent-Type: application/json\r\n{framing}\r\nConnection: close\r\n\r\n"), ct);
            if (reply.Chunked)
            {
                foreach (var chunk in body.Chunk(17))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(chunk.Length.ToString("X") + "\r\n"), ct);
                    await stream.WriteAsync(chunk, ct); await stream.WriteAsync("\r\n"u8.ToArray(), ct);
                }
                await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct);
            }
            else await stream.WriteAsync(body, ct);
        });
    }
    public ValueTask DisposeAsync() => fixture.DisposeAsync();
}

internal sealed class FixedDiscovery(params LocalEndpoint[] endpoints) : Watchdog.Infrastructure.IClashEndpointDiscovery
{
    public Task<IReadOnlyList<LocalEndpoint>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LocalEndpoint>>(endpoints);
}
