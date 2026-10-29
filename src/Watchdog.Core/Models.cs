using System.Text.Json.Serialization;

namespace Watchdog.Core;

public enum EndpointKind { UnixSocket, NamedPipe }

// Address is an opaque OS endpoint, not necessarily a filesystem path.
public sealed record LocalEndpoint(EndpointKind Kind, string Address)
{
    public static LocalEndpoint ForCurrentPlatform(string address) => new(
        OperatingSystem.IsWindows() ? EndpointKind.NamedPipe : EndpointKind.UnixSocket, address);
    public string Key => $"{Kind}:{Address}";
}

public sealed record Session(string Name, LocalEndpoint Endpoint);

public sealed record Agent
{
    [JsonPropertyName("agent")] public string? Kind { get; init; } = "codex";
    [JsonPropertyName("agent_status")] public string Status { get; init; } = "idle";
    public string PaneId { get; init; } = "w1:p1";
    public string TerminalId { get; init; } = "term_test";
    public string WorkspaceId { get; init; } = "w1";
    public string TabId { get; init; } = "w1:t1";
    public ulong? StateChangeSeq { get; init; } = 1;
    public ulong? Revision { get; init; }
    public string? Name { get; init; }
    public string? TerminalTitleStripped { get; init; }
    public string? ForegroundCwd { get; init; }
    public AgentSession? AgentSession { get; init; }
    public bool? InteractiveReady { get; init; }
    public bool? LaunchPending { get; init; }
    [JsonIgnore] public bool AcceptsInput => Status is "idle" or "done" && InteractiveReady != false && LaunchPending != true;
}

public sealed record AgentSession(string Kind, string Value);
public sealed record Observation(Session Session, Agent Agent, string Text = "", int? ForegroundPid = 100,
    string? Transcript = null, string? Error = null)
{
    public string Key => Session.Endpoint.Key + "|" + Agent.TerminalId;
    public string Occupant => $"{Agent.Kind}|{ForegroundPid ?? -1}|{Agent.AgentSession?.Value}";
}

public enum FailureReason { Capacity, Network }
public sealed record FailureEvidence(FailureReason Reason, string Message, string Fingerprint);
public sealed record Assessment(FailureEvidence? Evidence, string Detail)
{
    public static Assessment Skip(string detail) => new(null, detail);
    public static Assessment Failure(FailureEvidence evidence) => new(evidence,
        evidence.Reason == FailureReason.Capacity ? "模型容量不足 / 服务过载" : "网络连接 / 传输失败");
}
public sealed record AgentRow(string Id, string Title, string Location, string Kind, string State,
    string Detail, double? Deadline, int Attempts);
public sealed record MonitorEvent(DateTimeOffset Date, string Target, string Message);
public sealed class HerdrException(string message, bool definitelyNotSent = true, Exception? inner = null) : Exception(message, inner)
{
    public bool DefinitelyNotSent { get; } = definitelyNotSent;
}

public interface IHerdrService
{
    Task<IReadOnlyList<Session>> SessionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Agent>> AgentsAsync(Session session, CancellationToken cancellationToken);
    Task<Observation> ObserveAsync(Agent agent, Session session, bool refresh, CancellationToken cancellationToken);
    Task ResumeAsync(Observation observation, CancellationToken cancellationToken);
}

public sealed record ClashNode(string Name, string Type = "Vless", string? Now = null, string[]? All = null, string? SourceId = null, string? SourceName = null, string? OriginalName = null)
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { "direct", "reject", "rejectdrop", "pass", "compatible", "selector", "urltest", "fallback", "loadbalance", "relay" };
    [JsonIgnore] public bool IsProxyLeaf => All is null && !Excluded.Contains(Type);
    [JsonIgnore] public ProxyRegion? Region => IsProxyLeaf ? RegionResolver.Resolve(OriginalName ?? Name) : null;
}

public sealed record ProxySource(string Id, string Name, ClashNode[] Nodes, string? Error = null);

public sealed record ClashSnapshot(LocalEndpoint Endpoint, string Mode, int MixedPort, IReadOnlyDictionary<string, ClashNode> Nodes)
{
    public IReadOnlyList<ProxySource> Sources { get; init; } = [];
    public string? CurrentSourceId { get; init; }
    public string? ConfigurationRevision { get; init; }
    public string? Selected => Nodes.GetValueOrDefault("GLOBAL")?.Now;
    public bool CanSelect => Mode.Equals("global", StringComparison.OrdinalIgnoreCase) && MixedPort == 7890 &&
        Nodes.GetValueOrDefault("GLOBAL")?.Type.Equals("selector", StringComparison.OrdinalIgnoreCase) == true;
    public ClashNode[] Leaves => (Nodes.GetValueOrDefault("GLOBAL")?.All ?? [])
        .Distinct(StringComparer.Ordinal).Select(name => Nodes.GetValueOrDefault(name)).OfType<ClashNode>().Where(n => n.IsProxyLeaf)
        .OrderBy(n => n.Name, StringComparer.Ordinal).ToArray();
    public ClashNode[] Candidates => Leaves.Where(n => n.Region is not null).ToArray();
    public RegionAvailability[] Regions => Candidates.GroupBy(n => n.Region!.Id).Select(g => new RegionAvailability(g.First().Region!, g.Count()))
        .OrderBy(r => r.Region.Name, StringComparer.Ordinal).ToArray();
    public string? CurrentRegionId => SelectionPath.LastOrDefault() is { } leaf ? Nodes.GetValueOrDefault(leaf)?.Region?.Id : null;
    private string[] SelectionPath
    {
        get
        {
            List<string> path = []; HashSet<string> visited = [];
            var name = Selected;
            while (name is not null)
            {
                if (!visited.Add(name) || !Nodes.TryGetValue(name, out var node)) return [];
                path.Add(name);
                if (node.IsProxyLeaf) return path.ToArray();
                name = node.Now;
            }
            return [];
        }
    }
    public bool HasSameConfiguration(ClashSnapshot other) => ConfigurationRevision == other.ConfigurationRevision && Endpoint == other.Endpoint && Mode == other.Mode &&
        MixedPort == other.MixedPort && Nodes.GetValueOrDefault("GLOBAL")?.Type == other.Nodes.GetValueOrDefault("GLOBAL")?.Type &&
        (Nodes.GetValueOrDefault("GLOBAL")?.All ?? []).SequenceEqual(other.Nodes.GetValueOrDefault("GLOBAL")?.All ?? []) &&
        Nodes.Count == other.Nodes.Count && Nodes.All(pair => other.Nodes.TryGetValue(pair.Key, out var node) && pair.Value.Name == node.Name &&
            pair.Value.Type == node.Type && (pair.Value.All ?? []).SequenceEqual(node.All ?? []));
    public bool HasSameSelectionContext(ClashSnapshot other) => HasSameConfiguration(other) && Selected == other.Selected && SelectionPath.SequenceEqual(other.SelectionPath);
}

public enum ProxyProbeFailure { None, RequestFailed, SlowTimeout }
public sealed record ProxyProbe(bool Healthy, int? LatencyMs = null, ProxyProbeFailure Failure = ProxyProbeFailure.None);
public sealed record NodeDelay(string Name, int Milliseconds, string? RegionId = null, string? SourceName = null, string? OriginalName = null);
public enum ProxyPhase { Stopped, Checking, Healthy, Suspect, Testing, Switching, CoolingDown, Retrying, Unavailable }
public sealed record ProxyStatus
{
    public ProxyPhase Phase { get; init; } = ProxyPhase.Stopped;
    public string? Selected { get; init; }
    public string Detail { get; init; } = "启动服务后检查 127.0.0.1:7890";
    public double? HealthyAt { get; init; }
    public int? LatencyMs { get; init; }
    public int CandidateCount { get; init; }
    public int Completed { get; init; }
    public int Round { get; init; }
    public double? NextCheck { get; init; }
    public double? SwitchDeadline { get; init; }
    public IReadOnlyList<NodeDelay> Delays { get; init; } = [];
    public IReadOnlyList<RegionAvailability> Regions { get; init; } = [];
    public IReadOnlyList<string> RegionPriority { get; init; } = ["JP"];
    public string? CurrentRegionId { get; init; }
    public string? TestingRegionId { get; init; }
    public int UnknownNodeCount { get; init; }
    public IReadOnlyList<ProxySource> Sources { get; init; } = [];
    public string? CurrentSourceId { get; init; }
    public bool CatalogueLoaded { get; init; }
    public string? CatalogueError { get; init; }
    public bool PermitsAgent(double now) => Phase == ProxyPhase.Healthy && HealthyAt is { } at && now >= at && now - at <= 20;
}

public interface IClashService
{
    Task PrepareSourcesAsync(ProxyRegionPreferences preferences, CancellationToken ct, Action? changing = null) => Task.CompletedTask;
    Task RestoreSourcesAsync(CancellationToken ct) => Task.CompletedTask;
    Task<ClashSnapshot> SnapshotAsync(CancellationToken cancellationToken);
    Task<ProxyProbe> ProbeProxyAsync(CancellationToken cancellationToken);
    Task<int> DelayAsync(string node, LocalEndpoint endpoint, string url, CancellationToken cancellationToken);
    Task SelectAsync(string node, ClashSnapshot snapshot, CancellationToken cancellationToken);
}

public static class ProxyPolicy
{
    public const int MaximumLatencyMs = 800;
    public static readonly string[] TestUrls = ["https://www.gstatic.com/generate_204", "https://cp.cloudflare.com/generate_204"];
    public static NodeDelay[] Ranked(IEnumerable<NodeDelay> delays, ISet<string> allowed) => delays
        .Where(d => allowed.Contains(d.Name) && d.Milliseconds is > 0 and <= MaximumLatencyMs)
        .OrderBy(d => d.Milliseconds).ThenBy(d => d.Name, StringComparer.Ordinal).ToArray();
}

public sealed class MonotonicClock(TimeProvider? timeProvider = null)
{
    public TimeProvider Provider { get; } = timeProvider ?? TimeProvider.System;
    public double Now => Provider.GetTimestamp() / (double)Provider.TimestampFrequency;
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, Provider, cancellationToken);
}

public interface IEventSink { void Append(MonitorEvent value); }
public sealed record MonitorSnapshot(bool Running, string Connection, DateTimeOffset? LastScan,
    IReadOnlyList<AgentRow> Rows, IReadOnlyList<MonitorEvent> Events, ProxyStatus Proxy, bool ProxyRecoveryEnabled = true, bool AgentRecoveryEnabled = true);
