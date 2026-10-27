namespace Watchdog.Core;

public sealed class ProxyMonitor(IClashService service, MonotonicClock? clock = null, TimeSpan? interval = null) : IAsyncDisposable
{
    private readonly MonotonicClock clock = clock ?? new();
    private readonly TimeSpan interval = interval ?? TimeSpan.FromSeconds(10);
    private readonly object sync = new();
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private ProxyStatus status = new();
    private volatile bool running;
    private long epoch;
    private int generation, failures, round;
    private CancellationTokenSource? lifetime;
    private Task? loop;
    private string? lastEvent;
    public ProxyStatus Status => Volatile.Read(ref status);
    public bool Running => running;
    public long HealthEpoch => Interlocked.Read(ref epoch);
    public bool PermitsAgent => running && Status.PermitsAgent(clock.Now);
    public event Action<ProxyStatus>? StatusChanged;
    public event Action<string>? Event;

    public void Start(bool automatic = true)
    {
        lock (sync)
        {
            if (running) return;
            running = true; generation++; Interlocked.Increment(ref epoch); failures = 0; round = 0;
            lifetime = new(); status = new();
            Phase(generation, ProxyPhase.Checking, "正在检查 127.0.0.1:7890");
            if (automatic)
            {
                var token = lifetime.Token;
                loop = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            await CheckNowAsync(token);
                            await clock.Delay(TimeSpan.FromSeconds(Math.Max(0.01, (Status.NextCheck ?? clock.Now + interval.TotalSeconds) - clock.Now)), token);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }, CancellationToken.None);
            }
        }
    }

    public async Task StopAsync()
    {
        Task? pending;
        CancellationTokenSource? source;
        lock (sync)
        {
            if (!running) return;
            running = false; generation++; Interlocked.Increment(ref epoch);
            source = lifetime; lifetime = null; pending = loop; loop = null;
            status = status with { Phase = ProxyPhase.Stopped, HealthyAt = null, NextCheck = null, Detail = "代理监控已停止；保留当前节点" };
            StatusChanged?.Invoke(status);
        }
        source?.Cancel();
        if (pending is not null) await pending;
        // Also drain an explicitly requested check before releasing its cancellation source.
        await checkGate.WaitAsync(); checkGate.Release();
        source?.Dispose();
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        int token;
        CancellationToken localToken;
        lock (sync)
        {
            if (!running || lifetime is null) return;
            token = generation; localToken = lifetime.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, localToken);
        var ct = linked.Token;
        if (!await checkGate.WaitAsync(0, ct)) return;
        var started = clock.Now; var previousRound = round;
        Task<(ClashSnapshot?, string?)>? configTask = null;
        try
        {
            if (!Active(token)) return;
            configTask = ReadSnapshotAsync(ct);
            var probe = await service.ProbeProxyAsync(ct);
            if (!Active(token)) { await configTask; return; }
            failures = probe.Healthy ? 0 : failures + 1;
            if (probe.Healthy) Healthy(token, probe);
            else Phase(token, ProxyPhase.Suspect, $"代理请求失败；暂停 agent 续接（连续 {failures} 轮）");
            var (snapshot, error) = await configTask;
            if (!Active(token)) return;
            if (snapshot is not null) Apply(token, snapshot);
            if (probe.Healthy)
            {
                if (error is not null) Update(token, s => s with { Detail = "代理可用；" + error });
                else if (snapshot?.CanSelect == false) Update(token, s => s with { Detail = "代理可用；非全局模式，自动切换暂停" });
                return;
            }
            if (failures < 2) return;
            if (snapshot is null) { Phase(token, ProxyPhase.Unavailable, error ?? "等待 Clash 控制接口恢复"); return; }
            if (!snapshot.CanSelect) { Phase(token, ProxyPhase.Unavailable, "当前不是全局 GLOBAL 选择模式，暂停自动切换"); return; }
            await RecoverAsync(snapshot, token, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { Phase(token, ProxyPhase.Unavailable, "代理检查失败：" + e.Message); }
        finally
        {
            if (configTask is not null) { try { await configTask; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { } }
            Update(token, s => s with { NextCheck = round > previousRound ? clock.Now + interval.TotalSeconds : Math.Max(clock.Now + 0.01, started + interval.TotalSeconds) });
            checkGate.Release();
        }
    }

    private async Task RecoverAsync(ClashSnapshot initial, int token, CancellationToken ct)
    {
        round++;
        var candidates = initial.Candidates;
        Update(token, s => s with { Round = round, Delays = [], Completed = 0, CandidateCount = candidates.Length });
        if (candidates.Length == 0) { Phase(token, ProxyPhase.Retrying, "未发现可选择的日本节点；稍后重新检查"); return; }
        List<NodeDelay> delays = [];
        for (var index = 0; index < ProxyPolicy.TestUrls.Length; index++)
        {
            Update(token, s => s with { Completed = 0 });
            Phase(token, ProxyPhase.Testing, $"第 {round} 轮 · {(index == 0 ? "主地址" : "备用地址")} · 仅测试日本节点");
            delays = await MeasureAsync(candidates, initial.Endpoint, ProxyPolicy.TestUrls[index], token, ct);
            if (!Active(token)) return;
            if (delays.Count > 0) break;
        }
        var ranked = ProxyPolicy.Ranked(delays, candidates.Select(n => n.Name).ToHashSet(StringComparer.Ordinal));
        Update(token, s => s with { Delays = ranked });
        if (ranked.Length == 0) { Phase(token, ProxyPhase.Retrying, "全部日本节点超时或不可用；稍后重新测速"); return; }
        var expected = await CheckedSnapshotAsync(initial, token, ct);
        if (expected is null) return;
        var recovered = await service.ProbeProxyAsync(ct);
        if (!Active(token)) return;
        if (recovered.Healthy) { Healthy(token, recovered); return; }
        foreach (var candidate in ranked)
        {
            if (!Active(token)) return;
            var before = await CheckedSnapshotAsync(expected, token, ct);
            if (before is null) return;
            Phase(token, ProxyPhase.Switching, $"验证 {candidate.Name} · {candidate.Milliseconds} ms");
            Exception? writeError = null;
            if (before.Selected != candidate.Name)
            {
                try { ct.ThrowIfCancellationRequested(); await service.SelectAsync(candidate.Name, before, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { writeError = e; }
            }
            if (!Active(token)) return;
            var (after, error) = await ReadSnapshotAsync(ct);
            if (!Active(token)) return;
            if (after is null) { Phase(token, ProxyPhase.Unavailable, error ?? "无法确认节点切换结果"); return; }
            Apply(token, after);
            if (!after.HasSameConfiguration(before) || after.Selected != candidate.Name)
            {
                Phase(token, ProxyPhase.Retrying, writeError is null ? "节点选择或配置已变化；放弃本轮结果，稍后复核" : "切换未确认；稍后读取实际选择再重试");
                return;
            }
            Emit(token, $"已选择日本节点：{candidate.Name}（{candidate.Milliseconds} ms）；验证代理连通性");
            var verification = await service.ProbeProxyAsync(ct);
            if (!Active(token)) return;
            var confirmed = await CheckedSnapshotAsync(after, token, ct);
            if (confirmed is null) return;
            if (verification.Healthy)
            {
                Apply(token, confirmed); Healthy(token, verification);
                Emit(token, $"代理已恢复：{candidate.Name}；符合条件的 agent 将重新冷却 10 秒");
                return;
            }
            Emit(token, $"{candidate.Name} 实际代理请求失败，尝试下一可用日本节点");
            expected = confirmed;
        }
        Phase(token, ProxyPhase.Retrying, "本轮日本节点实际连通性均未通过；稍后重新测速");
    }

    private async Task<List<NodeDelay>> MeasureAsync(ClashNode[] nodes, LocalEndpoint endpoint, string url, int token, CancellationToken ct)
    {
        List<NodeDelay> results = [];
        foreach (var chunk in nodes.Chunk(4))
        {
            if (!Active(token)) break;
            var tasks = chunk.Select(async node =>
            {
                try { var delay = await service.DelayAsync(node.Name, endpoint, url, ct); return delay is > 0 and <= 5000 ? new NodeDelay(node.Name, delay) : null; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { return null; }
            }).ToList();
            try
            {
                while (tasks.Count > 0)
                {
                    var completed = await Task.WhenAny(tasks); tasks.Remove(completed);
                    var result = await completed;
                    if (result is not null) results.Add(result);
                    Update(token, s => s with { Completed = s.Completed + 1, Delays = ProxyPolicy.Ranked(results, nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal)) });
                }
            }
            finally { try { await Task.WhenAll(tasks); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { } }
        }
        return results;
    }

    private async Task<ClashSnapshot?> CheckedSnapshotAsync(ClashSnapshot expected, int token, CancellationToken ct)
    {
        var (fresh, error) = await ReadSnapshotAsync(ct);
        if (!Active(token)) return null;
        if (fresh is null) { Phase(token, ProxyPhase.Unavailable, error ?? "Clash 连接失败"); return null; }
        Apply(token, fresh);
        if (fresh.CanSelect && fresh.HasSameSelectionContext(expected)) return fresh;
        Phase(token, ProxyPhase.Retrying, "节点选择或配置已变化；放弃旧测速结果，稍后复核");
        return null;
    }
    private async Task<(ClashSnapshot?, string?)> ReadSnapshotAsync(CancellationToken ct)
    {
        try { return (await service.SnapshotAsync(ct), null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return (null, e.Message); }
    }
    private bool Active(int token) { lock (sync) return running && generation == token; }
    private void Update(int token, Func<ProxyStatus, ProxyStatus> change)
    {
        lock (sync) { if (!running || generation != token) return; Volatile.Write(ref status, change(status)); StatusChanged?.Invoke(status); }
    }
    private void Apply(int token, ClashSnapshot snapshot) => Update(token, s => s with { Selected = snapshot.Selected, CandidateCount = snapshot.Candidates.Length });
    private void Healthy(int token, ProxyProbe probe) { Update(token, s => s with { HealthyAt = clock.Now, LatencyMs = probe.LatencyMs }); Phase(token, ProxyPhase.Healthy, "代理连通；保持当前节点，故障后才切换"); }
    private void Phase(int token, ProxyPhase phase, string detail)
    {
        Update(token, s => { if (s.Phase == ProxyPhase.Healthy && phase != ProxyPhase.Healthy) Interlocked.Increment(ref epoch); return s with { Phase = phase, Detail = detail, HealthyAt = phase == ProxyPhase.Healthy ? s.HealthyAt : null }; });
        Emit(token, detail);
    }
    private void Emit(int token, string message)
    {
        lock (sync) { if (!running || generation != token || lastEvent == message) return; lastEvent = message; Event?.Invoke(message); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); checkGate.Dispose(); }
}
