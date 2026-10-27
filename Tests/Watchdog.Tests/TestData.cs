using Watchdog.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Watchdog.Tests;

internal static class TestData
{
    public const string Capacity = "Selected model is at capacity. Please try a different model.";
    public const string JapanFast = "🇯🇵日本高速02|BGP|CUCM", JapanSlow = "🇯🇵日本专线01|BGP|流媒体", OtherNode = "🇺🇸美国01";
    public static readonly Session Session = new("test", new(EndpointKind.UnixSocket, "/tmp/test.sock"));
    public static string Screen(string error = Capacity, string prompt = "Ask Codex to do anything", string prefix = "› Build a feature\n") =>
        prefix + "\n■ " + error + "\n\n› " + prompt + "\n\n  GPT-6.1-Sol high · ~/project\n  ? for shortcuts\n";
    public static Observation Observe(string? text = null, string status = "idle", ulong sequence = 1, string terminal = "term_test", int? pid = 100) =>
        new(Session, new Agent { Status = status, TerminalId = terminal, StateChangeSeq = sequence }, text ?? Screen(), pid);
    public static ClashSnapshot Clash(string selected = OtherNode, string mode = "global", string endpoint = "/tmp/mock.sock", string[]? names = null)
    {
        names ??= [JapanFast, JapanSlow, OtherNode];
        var nodes = names.ToDictionary(n => n, n => new ClashNode(n));
        nodes["DIRECT"] = new("DIRECT", "Direct"); nodes["自动选择"] = new("自动选择", "URLTest", OtherNode, names);
        nodes["GLOBAL"] = new("GLOBAL", "Selector", selected, [.. names, "DIRECT", "自动选择"]);
        return new(new(EndpointKind.UnixSocket, endpoint), mode, 7890, nodes);
    }
    public static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    public static string TempDirectory()
    { var path = Path.Combine(Path.GetTempPath(), "watchdog-qa-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}

internal sealed class ManualTime : TimeProvider
{
    private long ticks;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(GetTimestamp());
    public double Now { set => Interlocked.Exchange(ref ticks, (long)(value * 1000)); }
}

internal sealed class FakeHerdr : IHerdrService
{
    public Observation Value = TestData.Observe();
    public int Sent;
    public bool Offline, Replacement, SlowRefresh;
    public TaskCompletionSource RefreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<IReadOnlyList<Session>> SessionsAsync(CancellationToken ct) => Offline ? throw new HerdrException("offline") : Task.FromResult<IReadOnlyList<Session>>([TestData.Session]);
    public Task<IReadOnlyList<Agent>> AgentsAsync(Session s, CancellationToken ct) => Task.FromResult<IReadOnlyList<Agent>>([Value.Agent]);
    public async Task<Observation> ObserveAsync(Agent a, Session s, bool refresh, CancellationToken ct)
    {
        if (refresh) { RefreshStarted.TrySetResult(); if (SlowRefresh) await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        return refresh && Replacement ? TestData.Observe(pid: 999) : Value;
    }
    public Task ResumeAsync(Observation o, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Sent); return Task.CompletedTask; }
}

internal sealed class FakeClash : IClashService
{
    private readonly object sync = new();
    public ClashSnapshot State = TestData.Clash();
    public bool Healthy = true, FailSnapshot, SelectionError, SlowTests;
    public HashSet<string> HealthyNodes = [];
    public Dictionary<string, int> Delays = new() { [TestData.JapanFast] = 50, [TestData.JapanSlow] = 120, [TestData.OtherNode] = 1 };
    public Dictionary<string, int>? Fallback;
    public ClashSnapshot? Mutation;
    public ProxyProbeFailure ProbeFailure;
    public Func<string, CancellationToken, Task<ProxyProbe>>? Probe;
    public Func<string, string, CancellationToken, Task<int>>? Measure;
    public List<string> Selected = [], Tested = [], TestedUrls = [];
    public int ActiveTests, MaximumConcurrent;
    public Task<ClashSnapshot> SnapshotAsync(CancellationToken ct)
    { lock (sync) return FailSnapshot ? throw new HerdrException("mock offline") : Task.FromResult(State); }
    public Task<ProxyProbe> ProbeProxyAsync(CancellationToken ct)
    {
        lock (sync)
        {
            var selected = State.Selected ?? "";
            if (Probe is not null) return Probe(selected, ct);
            var healthy = Healthy || HealthyNodes.Contains(selected);
            return Task.FromResult(new ProxyProbe(healthy, healthy ? 100 : null, healthy ? ProxyProbeFailure.None : ProbeFailure));
        }
    }
    public async Task<int> DelayAsync(string node, LocalEndpoint endpoint, string url, CancellationToken ct)
    {
        lock (sync) { Tested.Add(node); TestedUrls.Add(url); ActiveTests++; MaximumConcurrent = Math.Max(MaximumConcurrent, ActiveTests); }
        try
        {
            if (Measure is not null) return await Measure(node, url, ct);
            await Task.Delay(SlowTests ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(2), ct);
            lock (sync)
            {
                if (Mutation is not null) { State = Mutation; Mutation = null; }
                var values = url == ProxyPolicy.TestUrls[1] ? Fallback ?? Delays : Delays;
                return values.TryGetValue(node, out var value) ? value : throw new HerdrException("timeout");
            }
        }
        finally { lock (sync) ActiveTests--; }
    }
    public Task SelectAsync(string node, ClashSnapshot snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            Selected.Add(node); State = TestData.Clash(node, State.Mode, State.Endpoint.Address,
                State.Nodes["GLOBAL"].All!.Where(n => n is not "DIRECT" and not "自动选择").ToArray());
            if (SelectionError) throw new HerdrException("PUT timeout");
        }
        return Task.CompletedTask;
    }
}
