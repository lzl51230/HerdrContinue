using Watchdog.Core;
using Watchdog.Infrastructure;

Console.OutputEncoding = System.Text.Encoding.UTF8;
try
{
    var service = new LiveHerdrService();
    if (args.Contains("--clash-readonly"))
    {
        var clash = new LiveClashService();
        var snapshot = await clash.SnapshotAsync(CancellationToken.None);
        var probe = await clash.ProbeProxyAsync(CancellationToken.None);
        Console.WriteLine($"Clash: mode={snapshot.Mode}, mixed-port={snapshot.MixedPort}, selected={snapshot.Selected}, regions={snapshot.Regions.Length}, recognized leaves={snapshot.Candidates.Length}, unknown={snapshot.Leaves.Length - snapshot.Candidates.Length}");
        foreach (var region in snapshot.Regions) Console.WriteLine($"  {region.Region.Id} {region.Region.Name}: {region.NodeCount}");
        Console.WriteLine($"Proxy reachable={probe.Healthy}, latency={probe.LatencyMs} ms; no node testing or switching performed");
    }
    else if (args.Contains("--monitor-test"))
    {
        var index = Array.IndexOf(args, "--socket");
        if (index < 0 || index + 1 >= args.Length || !args[index + 1].Contains("watchdog-qa-", StringComparison.Ordinal))
            throw new HerdrException("Test monitor only accepts an isolated watchdog-qa- endpoint");
        var isolated = new IsolatedService(new("isolated-test", LocalEndpoint.ForCurrentPlatform(args[index + 1])), service);
        var log = new JsonEventLog(Path.Combine(Path.GetTempPath(), "watchdog-qa-probe-" + Guid.NewGuid().ToString("N"), "events.jsonl"));
        await using var monitor = new MonitorEngine(isolated, new HealthyTestProxy(), log, pollInterval: TimeSpan.FromMilliseconds(250));
        await monitor.StartAsync(); await Task.Delay(TimeSpan.FromSeconds(15)); await monitor.StopAsync();
        foreach (var value in monitor.Snapshot.Events) Console.WriteLine($"{value.Target}: {value.Message}");
        if (!monitor.Snapshot.Events.Any(e => e.Message.Contains("Herdr 已确认提交"))) throw new HerdrException("No confirmed test submission");
    }
    else
    {
        var sessions = await service.SessionsAsync(CancellationToken.None);
        Console.WriteLine($"Read-only discovery: {sessions.Count} running session(s)");
        foreach (var session in sessions)
            foreach (var agent in await service.AgentsAsync(session, CancellationToken.None))
            {
                var observation = await service.ObserveAsync(agent, session, false, CancellationToken.None);
                Console.WriteLine($"{session.Name} {agent.PaneId} {agent.Kind} {agent.Status}: {new FailureClassifier().Assess(observation).Detail}");
            }
    }
    return 0;
}
catch (Exception e) { Console.Error.WriteLine("Probe failed: " + e.Message); return 1; }

internal sealed class IsolatedService(Session session, IHerdrService inner) : IHerdrService
{
    public Task<IReadOnlyList<Session>> SessionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Session>>([session]);
    public Task<IReadOnlyList<Agent>> AgentsAsync(Session s, CancellationToken ct) => inner.AgentsAsync(s, ct);
    public Task<Observation> ObserveAsync(Agent a, Session s, bool refresh, CancellationToken ct) => inner.ObserveAsync(a, s, refresh, ct);
    public Task ResumeAsync(Observation o, CancellationToken ct) => inner.ResumeAsync(o, ct);
}
internal sealed class HealthyTestProxy : IClashService
{
    public Task<ClashSnapshot> SnapshotAsync(CancellationToken ct) => throw new HerdrException("隔离测试不读取实际 Clash");
    public Task<ProxyProbe> ProbeProxyAsync(CancellationToken ct) => Task.FromResult(new ProxyProbe(true));
    public Task<int> DelayAsync(string n, LocalEndpoint e, string u, CancellationToken ct) => throw new InvalidOperationException();
    public Task SelectAsync(string n, ClashSnapshot s, CancellationToken ct) => throw new InvalidOperationException();
}
