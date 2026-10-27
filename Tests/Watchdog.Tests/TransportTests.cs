using System.Text;
using System.Text.Json;
using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;

namespace Watchdog.Tests;

public sealed class TransportTests
{
    public static TheoryData<EndpointKind> Transports => OperatingSystem.IsWindows() ? new() { EndpointKind.NamedPipe } : new() { EndpointKind.UnixSocket, EndpointKind.NamedPipe };
    [Theory, MemberData(nameof(Transports))]
    public async Task PromptWireFormatAndFragmentedResponse(EndpointKind kind)
    {
        JsonElement received = default;
        await using var fixture = new LocalFixture(kind, async (stream, ct) =>
        {
            using var document = JsonDocument.Parse(await LocalFixture.ReadLineAsync(stream, ct)); received = document.RootElement.Clone();
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = received.GetProperty("id").GetString(), result = new { type = "agent_info", text = "继续🇯🇵" } }) + "\n");
            foreach (var b in bytes) await stream.WriteAsync(new[] { b }, ct);
        });
        var result = await new SocketRpc().CallAsync(fixture.Endpoint, "agent.prompt", new Dictionary<string, object?> { ["target"] = "w1:p1", ["text"] = "继续" }, true);
        Assert.Equal("继续🇯🇵", result.GetProperty("text").GetString()); var args = received.GetProperty("params");
        Assert.Equal("继续", args.GetProperty("text").GetString()); Assert.Equal("w1:p1", args.GetProperty("target").GetString());
        Assert.Equal("working", args.GetProperty("wait").GetProperty("until")[0].GetString()); Assert.Equal(5000, args.GetProperty("wait").GetProperty("timeout_ms").GetInt32());
    }
    [Theory, MemberData(nameof(Transports))]
    public async Task BlockedIsDefiniteRefusal(EndpointKind kind)
    {
        await using var fixture = new LocalFixture(kind, async (stream, ct) =>
        { using var d = JsonDocument.Parse(await LocalFixture.ReadLineAsync(stream, ct)); await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = d.RootElement.GetProperty("id").GetString(), error = new { code = "agent_blocked", message = "waiting for approval" } }) + "\n"), ct); });
        var error = await Assert.ThrowsAsync<HerdrException>(() => new SocketRpc().CallAsync(fixture.Endpoint, "agent.prompt")); Assert.True(error.DefinitelyNotSent);
    }
    [Theory, MemberData(nameof(Transports))]
    public async Task DisconnectAfterWriteIsAmbiguous(EndpointKind kind)
    {
        await using var fixture = new LocalFixture(kind, async (stream, ct) => { await LocalFixture.ReadLineAsync(stream, ct); });
        Assert.False((await Assert.ThrowsAsync<HerdrException>(() => new SocketRpc().CallAsync(fixture.Endpoint, "agent.prompt"))).DefinitelyNotSent);
    }
    [Theory, MemberData(nameof(Transports))]
    public async Task DeadlineAfterWriteIsAmbiguous(EndpointKind kind)
    {
        await using var fixture = new LocalFixture(kind, async (stream, ct) => { await LocalFixture.ReadLineAsync(stream, ct); await Task.Delay(Timeout.InfiniteTimeSpan, ct); });
        Assert.False((await Assert.ThrowsAsync<HerdrException>(() => new SocketRpc(timeout: TimeSpan.FromMilliseconds(150)).CallAsync(fixture.Endpoint, "agent.prompt"))).DefinitelyNotSent);
    }
    [Theory, MemberData(nameof(Transports))]
    public async Task CancellationInterruptsPendingRead(EndpointKind kind)
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new LocalFixture(kind, async (stream, ct) => { await LocalFixture.ReadLineAsync(stream, ct); read.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); });
        using var cancel = new CancellationTokenSource(); var task = new SocketRpc().CallAsync(fixture.Endpoint, "agent.prompt", cancellationToken: cancel.Token);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(1)));
    }
    [Theory, MemberData(nameof(Transports))]
    public async Task UnavailableEndpointIsDefinitelyUnsent(EndpointKind kind)
    {
        var endpoint = new LocalEndpoint(kind, kind == EndpointKind.UnixSocket ? "/tmp/watchdog-not-present-" + Guid.NewGuid().ToString("N") : "watchdog-not-present-" + Guid.NewGuid());
        Assert.True((await Assert.ThrowsAsync<HerdrException>(() => new SocketRpc(timeout: TimeSpan.FromMilliseconds(100)).CallAsync(endpoint, "agent.prompt"))).DefinitelyNotSent);
    }
    [Theory, MemberData(nameof(Transports))]
    public async Task InvalidResponseIdentityIsAmbiguous(EndpointKind kind)
    {
        await using var fixture = new LocalFixture(kind, async (stream, ct) => { await LocalFixture.ReadLineAsync(stream, ct); await stream.WriteAsync("{\"id\":\"wrong\",\"result\":{}}\n"u8.ToArray(), ct); });
        Assert.False((await Assert.ThrowsAsync<HerdrException>(() => new SocketRpc().CallAsync(fixture.Endpoint, "agent.prompt"))).DefinitelyNotSent);
    }
    [Theory, MemberData(nameof(Transports))]
    public async Task OversizeResponseIsRejected(EndpointKind kind)
    {
        await using var fixture = new LocalFixture(kind, async (stream, ct) => { await LocalFixture.ReadLineAsync(stream, ct); await stream.WriteAsync(new byte[SocketRpc.MaximumResponseBytes], ct); });
        Assert.Contains("响应过大", (await Assert.ThrowsAsync<HerdrException>(() => new SocketRpc().CallAsync(fixture.Endpoint, "agent.get"))).Message);
    }
}
