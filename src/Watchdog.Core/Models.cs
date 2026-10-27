using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

public sealed record ClashNode(string Name, string Type = "Vless", string? Now = null, string[]? All = null)
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { "direct", "reject", "rejectdrop", "pass", "compatible", "selector", "urltest", "fallback", "loadbalance", "relay" };
    public bool IsJapaneseLeaf => All is null && !Excluded.Contains(Type) &&
        Regex.IsMatch(Name, @"🇯🇵|日本|(?i:\bjapan\b|(?<![a-z0-9])jp(?![a-z0-9]))", RegexOptions.CultureInvariant);
}

public sealed record ClashSnapshot(LocalEndpoint Endpoint, string Mode, int MixedPort, IReadOnlyDictionary<string, ClashNode> Nodes)
{
    public string? Selected => Nodes.GetValueOrDefault("GLOBAL")?.Now;
    public bool CanSelect => Mode.Equals("global", StringComparison.OrdinalIgnoreCase) && MixedPort == 7890 &&
        Nodes.GetValueOrDefault("GLOBAL")?.Type.Equals("selector", StringComparison.OrdinalIgnoreCase) == true;
    public ClashNode[] Candidates => (Nodes.GetValueOrDefault("GLOBAL")?.All ?? [])
        .Select(name => Nodes.GetValueOrDefault(name)).OfType<ClashNode>().Where(n => n.IsJapaneseLeaf)
        .OrderBy(n => n.Name, StringComparer.Ordinal).ToArray();
    public bool HasSameConfiguration(ClashSnapshot other) => Endpoint == other.Endpoint && Mode == other.Mode &&
        MixedPort == other.MixedPort && Nodes.GetValueOrDefault("GLOBAL")?.Type == other.Nodes.GetValueOrDefault("GLOBAL")?.Type &&
        (Nodes.GetValueOrDefault("GLOBAL")?.All ?? []).SequenceEqual(other.Nodes.GetValueOrDefault("GLOBAL")?.All ?? []) &&
        Candidates.SequenceEqual(other.Candidates);
    public bool HasSameSelectionContext(ClashSnapshot other) => HasSameConfiguration(other) && Selected == other.Selected;
}

public sealed record ProxyProbe(bool Healthy, int? LatencyMs = null);
public sealed record NodeDelay(string Name, int Milliseconds);
public enum ProxyPhase { Stopped, Checking, Healthy, Suspect, Testing, Switching, Retrying, Unavailable }
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
    public IReadOnlyList<NodeDelay> Delays { get; init; } = [];
    public bool PermitsAgent(double now) => Phase == ProxyPhase.Healthy && HealthyAt is { } at && now >= at && now - at <= 20;
}

public interface IClashService
{
    Task<ClashSnapshot> SnapshotAsync(CancellationToken cancellationToken);
    Task<ProxyProbe> ProbeProxyAsync(CancellationToken cancellationToken);
    Task<int> DelayAsync(string node, LocalEndpoint endpoint, string url, CancellationToken cancellationToken);
    Task SelectAsync(string node, ClashSnapshot snapshot, CancellationToken cancellationToken);
}

public static class ProxyPolicy
{
    public static readonly string[] TestUrls = ["https://www.gstatic.com/generate_204", "https://cp.cloudflare.com/generate_204"];
    public static NodeDelay[] Ranked(IEnumerable<NodeDelay> delays, ISet<string> allowed) => delays
        .Where(d => allowed.Contains(d.Name) && d.Milliseconds is > 0 and <= 5000)
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
    IReadOnlyList<AgentRow> Rows, IReadOnlyList<MonitorEvent> Events, ProxyStatus Proxy);
