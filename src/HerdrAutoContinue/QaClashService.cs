#if DEBUG
using System.Text.Json;
using Watchdog.Core;

namespace HerdrAutoContinue;

// Isolated transport fixture; never opens or modifies the user's Clash controller.
internal sealed class QaClashService(bool recover, string directory) : IClashService
{
    private readonly object sync = new();
    private string selected = "验收日本01";
    private readonly List<string> tested = [], selections = [];
    private static readonly string[] Names = ["验收日本01", "验收日本02", "验收香港01", "验收香港02", "验收新加坡01", "验收美国01"];
    public Task<ClashSnapshot> SnapshotAsync(CancellationToken ct)
    {
        lock (sync)
        {
            var nodes = Names.ToDictionary(n => n, n => new ClashNode(n));
            nodes["GLOBAL"] = new("GLOBAL", "Selector", selected, Names);
            return Task.FromResult(new ClashSnapshot(new(EndpointKind.NamedPipe, "watchdog-qa-clash"), "global", 7890, nodes));
        }
    }
    public Task<ProxyProbe> ProbeProxyAsync(CancellationToken ct)
    { lock (sync) return Task.FromResult(new ProxyProbe(!recover || selected == "验收香港01", 15)); }
    public async Task<int> DelayAsync(string node, LocalEndpoint endpoint, string url, CancellationToken ct)
    {
        if (!recover) throw new InvalidOperationException("Healthy QA proxy never tests nodes");
        await Task.Delay(20, ct);
        lock (sync)
        {
            tested.Add(node); Trace();
            return RegionResolver.Resolve(node)?.Id switch { "JP" => throw new HerdrException("QA timeout"), "HK" => node.EndsWith("01") ? 30 : 80, _ => 10 };
        }
    }
    public Task SelectAsync(string node, ClashSnapshot snapshot, CancellationToken ct)
    {
        if (!recover) throw new InvalidOperationException("Healthy QA proxy never switches nodes");
        ct.ThrowIfCancellationRequested();
        lock (sync) { selected = node; selections.Add(node); Trace(); }
        return Task.CompletedTask;
    }
    private void Trace()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"clash-trace-{Environment.ProcessId}.json"), JsonSerializer.Serialize(new { tested, selected = selections }));
    }
}
#endif
