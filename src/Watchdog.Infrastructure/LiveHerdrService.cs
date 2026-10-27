using System.Text;
using System.Text.Json;
using Watchdog.Core;

namespace Watchdog.Infrastructure;

public sealed class LiveHerdrService(string? binary = null, ILocalConnectionFactory? connections = null,
    string? transcriptRoot = null) : IHerdrService
{
    private readonly ProcessRunner processes = new();
    private readonly SocketRpc rpc = new(connections);
    private readonly SocketRpc promptRpc = new(connections, TimeSpan.FromSeconds(8));
    private readonly TranscriptReader transcripts = new(transcriptRoot);

    public async Task<IReadOnlyList<Session>> SessionsAsync(CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(HerdrBinary.Resolve(binary), ["session", "list", "--json"], TimeSpan.FromSeconds(3), cancellationToken, true);
        if (result.ExitCode != 0) throw new HerdrException("Herdr 会话发现失败：" + result.Error[..Math.Min(300, result.Error.Length)]);
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.GetProperty("sessions").EnumerateArray()
            .Where(s => s.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True)
            .Select(s => new Session(s.GetProperty("name").GetString()!, LocalEndpoint.ForCurrentPlatform(s.GetProperty("socket_path").GetString()!))).ToArray();
    }
    public async Task<IReadOnlyList<Agent>> AgentsAsync(Session session, CancellationToken cancellationToken) =>
        (await rpc.CallAsync(session.Endpoint, "agent.list", cancellationToken: cancellationToken)).GetProperty("agents").EnumerateArray().Select(ParseAgent).ToArray();

    public async Task<Observation> ObserveAsync(Agent original, Session session, bool refresh, CancellationToken cancellationToken)
    {
        var agent = refresh ? ParseAgent((await rpc.CallAsync(session.Endpoint, "agent.get", new Dictionary<string, object?> { ["target"] = original.PaneId }, cancellationToken: cancellationToken)).GetProperty("agent")) : original;
        if (!agent.AcceptsInput) return new(session, agent, ForegroundPid: null);
        var read = rpc.CallAsync(session.Endpoint, "agent.read", new Dictionary<string, object?> { ["target"] = agent.PaneId, ["source"] = "detection", ["format"] = "text" }, cancellationToken: cancellationToken);
        var process = rpc.CallAsync(session.Endpoint, "pane.process_info", new Dictionary<string, object?> { ["pane_id"] = agent.PaneId }, cancellationToken: cancellationToken);
        await Task.WhenAll(read, process);
        var text = (await read).GetProperty("read").GetProperty("text").GetString() ?? "";
        var info = (await process).GetProperty("process_info");
        int? pid = info.TryGetProperty("foreground_process_group_id", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value) ? value : null;
        var transcript = agent.Kind == "omp" && agent.AgentSession is { Kind: "path" } reference ? transcripts.Read(reference.Value) : null;
        return new(session, agent, text, pid, transcript);
    }
    public async Task ResumeAsync(Observation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await promptRpc.CallAsync(observation.Session.Endpoint, "agent.prompt", new Dictionary<string, object?>
        { ["target"] = observation.Agent.PaneId, ["text"] = "继续" }, true, cancellationToken);
    }

    public static Agent ParseAgent(JsonElement element)
    {
        foreach (var name in new[] { "agent_status", "pane_id", "terminal_id", "workspace_id", "tab_id" })
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new HerdrException("Herdr agent 缺少可靠身份：" + name);
        var agent = element.Deserialize<Agent>(WireJson.Options) ?? throw new HerdrException("Herdr agent 格式错误");
        return agent with
        {
            Kind = element.TryGetProperty("agent", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null,
            StateChangeSeq = element.TryGetProperty("state_change_seq", out var sequence) && sequence.ValueKind == JsonValueKind.Number && sequence.TryGetUInt64(out var seq) ? seq : null
        };
    }
}

public sealed class TranscriptReader(string? root = null)
{
    private readonly string root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omp", "agent", "sessions");
    public string? Read(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            var actualRoot = Canonical(root); var actual = Canonical(path);
            var relative = Path.GetRelativePath(actualRoot, actual);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !Path.GetExtension(actual).Equals(".jsonl", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
            using var file = new FileStream(actual, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, file.Length - 262144); file.Position = start;
            var buffer = new byte[262144]; var count = 0;
            // Read at most the bounded tail, even if the writer appends while it is being read.
            while (count < buffer.Length) { var n = file.Read(buffer, count, buffer.Length - count); if (n == 0) break; count += n; }
            var offset = start > 0 ? Array.IndexOf(buffer, (byte)10, 0, count) + 1 : 0;
            if (start > 0 && offset == 0) return null;
            return new UTF8Encoding(false, true).GetString(buffer, offset, count - offset);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException or NotSupportedException) { return null; }
    }
    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null) current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("无法解析会话路径");
        }
        return Path.GetFullPath(current);
    }
}
