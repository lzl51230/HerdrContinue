using System.Text.Json;
using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class ClashHttpTests
{
    private static HttpReply Reply(HttpRequest request, Func<string> selected, Action<string> select)
    {
        if (request.Resource == "/version") return new(200, "{\"meta\":true}");
        if (request.Resource == "/configs") return new(200, "{\"mixed-port\":7890,\"mode\":\"global\"}");
        if (request.Resource == "/proxies") return new(200, JsonSerializer.Serialize(new { proxies = Clash(selected()).Nodes }, WireJson.Options), true);
        if (request.Resource.StartsWith("/proxies/") && request.Resource.Contains("/delay?")) return new(200, "{\"delay\":42}", true);
        if (request.Method == "PUT" && request.Resource == "/proxies/GLOBAL") { using var d = JsonDocument.Parse(request.Body); select(d.RootElement.GetProperty("name").GetString()!); return new(204, ""); }
        return new(404, "{}");
    }
    [Theory, MemberData(nameof(TransportTests.Transports), MemberType = typeof(TransportTests))]
    public async Task DiscoveryEncodedDelayAndSelectionReadback(EndpointKind kind)
    {
        var selected = OtherNode;
        await using var fixture = new HttpFixture(kind, request => Reply(request, () => selected, s => selected = s));
        var service = new LiveClashService(new FixedDiscovery(fixture.Endpoint)); var before = await service.SnapshotAsync(default);
        Assert.Equal(OtherNode, before.Selected); Assert.Equal(2, before.Candidates.Length);
        Assert.Equal(42, await service.DelayAsync(JapanFast, fixture.Endpoint, ProxyPolicy.TestUrls[0], default));
        await service.SelectAsync(JapanFast, before, default); Assert.Equal(JapanFast, (await service.SnapshotAsync(default)).Selected);
        var delay = Assert.Single(fixture.Requests, r => r.Resource.Contains("/delay?"));
        Assert.Contains(Uri.EscapeDataString(JapanFast), delay.Resource); Assert.Contains("expected=204", delay.Resource); Assert.Contains(Uri.EscapeDataString(ProxyPolicy.TestUrls[0]), delay.Resource);
    }
    [Theory, MemberData(nameof(TransportTests.Transports), MemberType = typeof(TransportTests))]
    public async Task NonJapaneseAndGroupSelectionsAreRefusedBeforeHttp(EndpointKind kind)
    {
        await using var fixture = new HttpFixture(kind, request => Reply(request, () => OtherNode, _ => { }));
        var service = new LiveClashService(new FixedDiscovery(fixture.Endpoint)); var snapshot = await service.SnapshotAsync(default);
        foreach (var node in new[] { OtherNode, "DIRECT", "自动选择" }) await Assert.ThrowsAsync<HerdrException>(() => service.SelectAsync(node, snapshot, default));
        Assert.DoesNotContain(fixture.Requests, r => r.Method == "PUT");
    }
    [Theory, MemberData(nameof(TransportTests.Transports), MemberType = typeof(TransportTests))]
    public async Task ControllerIsRediscoveredAfterRestart(EndpointKind kind)
    {
        await using var first = new HttpFixture(kind, r => Reply(r, () => OtherNode, _ => { }));
        await using var next = new HttpFixture(kind, r => Reply(r, () => JapanFast, _ => { }));
        var discovery = new MutableDiscovery(first.Endpoint); var service = new LiveClashService(discovery);
        var before = await service.SnapshotAsync(default); discovery.Endpoint = next.Endpoint; var after = await service.SnapshotAsync(default);
        Assert.Equal(JapanFast, after.Selected); Assert.False(before.HasSameConfiguration(after));
    }
    [Theory, MemberData(nameof(TransportTests.Transports), MemberType = typeof(TransportTests))]
    public async Task MultipleValidControllersAreRejected(EndpointKind kind)
    {
        await using var a = new HttpFixture(kind, r => Reply(r, () => OtherNode, _ => { })); await using var b = new HttpFixture(kind, r => Reply(r, () => OtherNode, _ => { }));
        Assert.Contains("多个", (await Assert.ThrowsAsync<HerdrException>(() => new LiveClashService(new FixedDiscovery(a.Endpoint, b.Endpoint)).SnapshotAsync(default))).Message);
    }
    private sealed class MutableDiscovery(LocalEndpoint endpoint) : IClashEndpointDiscovery
    { public LocalEndpoint Endpoint = endpoint; public Task<IReadOnlyList<LocalEndpoint>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LocalEndpoint>>([Endpoint]); }
}
