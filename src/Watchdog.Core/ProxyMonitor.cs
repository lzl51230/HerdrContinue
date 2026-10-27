namespace Watchdog.Core;

public sealed class ProxyMonitor(IClashService service, MonotonicClock? clock = null, TimeSpan? interval = null,
    ProxyRegionPreferences? preferences = null) : IAsyncDisposable
{
    private readonly MonotonicClock clock = clock ?? new();
    private readonly TimeSpan interval = interval ?? TimeSpan.FromSeconds(10);
    private readonly object sync = new();
    private readonly SemaphoreSlim checkGate = new(1, 1), preferencesGate = new(1, 1), wake = new(0, 1);
    private readonly CancellationTokenSource catalogueLifetime = new();
    private CancellationTokenSource configuration = new();
    private ProxyRegionPreferences preferences = preferences ?? ProxyRegionPreferences.Default;
    private ProxyStatus status = new() { RegionPriority = (preferences ?? ProxyRegionPreferences.Default).PriorityRegionIds };
    private volatile bool running;
    private bool disposed;
    private long epoch;
    private int generation, failures, round;
    private CancellationTokenSource? lifetime;
    private Task? loop;
    private string? lastEvent;
    public ProxyStatus Status => Volatile.Read(ref status);
    public ProxyRegionPreferences Preferences => Volatile.Read(ref preferences);
    public bool Running => running;
    public long HealthEpoch => Interlocked.Read(ref epoch);
    public bool PermitsAgent => running && Status.PermitsAgent(clock.Now);
    public event Action<ProxyStatus>? StatusChanged;
    public event Action<string>? Event;

    public void Start(bool automatic = true)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running) return;
            running = true; generation++; Interlocked.Increment(ref epoch); failures = 0; round = 0;
            while (wake.Wait(0)) { }
            lifetime = new();
            status = status with { Phase = ProxyPhase.Checking, HealthyAt = null, NextCheck = null, Round = 0,
                Delays = [], TestingRegionId = null, Completed = 0, RegionPriority = preferences.PriorityRegionIds };
            Phase(generation, ProxyPhase.Checking, "正在检查 127.0.0.1:7890");
            if (automatic)
            {
                var ct = lifetime.Token;
                loop = Task.Run(async () =>
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            await CheckNowAsync(ct);
                            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var delay = clock.Delay(TimeSpan.FromSeconds(Math.Max(0.01,
                                (Status.NextCheck ?? clock.Now + interval.TotalSeconds) - clock.Now)), waiting.Token);
                            var changed = wake.WaitAsync(waiting.Token);
                            await Task.WhenAny(delay, changed);
                            waiting.Cancel();
                            try { await Task.WhenAll(delay, changed); } catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                }, CancellationToken.None);
            }
        }
    }

    public async Task StopAsync()
    {
        Task? pending; CancellationTokenSource? source;
        lock (sync)
        {
            if (!running) return;
            running = false; generation++; Interlocked.Increment(ref epoch);
            source = lifetime; lifetime = null; pending = loop; loop = null;
            status = status with { Phase = ProxyPhase.Stopped, HealthyAt = null, NextCheck = null,
                TestingRegionId = null, Detail = "代理监控已停止；保留当前节点" };
            StatusChanged?.Invoke(status);
        }
        source?.Cancel();
        if (pending is not null) await pending;
        await checkGate.WaitAsync(); checkGate.Release();
        source?.Dispose();
    }

    public async Task ApplyPreferencesAsync(ProxyRegionPreferences value, CancellationToken cancellationToken = default)
    {
        await preferencesGate.WaitAsync(cancellationToken);
        try
        {
            CancellationTokenSource previous; bool notifyLoop;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (preferences.HasSameOrder(value)) return;
                previous = configuration; configuration = new(); generation++;
                notifyLoop = running;
                Volatile.Write(ref preferences, value);
                status = status with { RegionPriority = value.PriorityRegionIds, TestingRegionId = null,
                    Delays = [], Completed = 0, NextCheck = running ? clock.Now : null };
                if (running && status.Phase != ProxyPhase.Healthy)
                    status = status with { Phase = ProxyPhase.Checking, Detail = "地区设置已更新，重新检查代理" };
                StatusChanged?.Invoke(status);
            }
            previous.Cancel();
            // Drain old reads, measurements and any already-started write before waking a fresh cycle.
            await checkGate.WaitAsync(); checkGate.Release(); previous.Dispose();
            if (notifyLoop) { try { wake.Release(); } catch (SemaphoreFullException) { } }
        }
        finally { preferencesGate.Release(); }
    }

    public async Task RefreshCatalogueAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, catalogueLifetime.Token);
        var ct = linked.Token;
        await checkGate.WaitAsync(ct);
        try
        {
            var snapshot = await service.SnapshotAsync(ct);
            ct.ThrowIfCancellationRequested();
            lock (sync) { if (!disposed) { status = WithCatalogue(status, snapshot); StatusChanged?.Invoke(status); } }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            lock (sync) { if (!disposed) { status = status with { CatalogueError = e.Message }; StatusChanged?.Invoke(status); } }
        }
        finally { checkGate.Release(); }
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        int token; CancellationToken localToken, policyToken;
        lock (sync)
        {
            if (!running || lifetime is null) return;
            token = generation; localToken = lifetime.Token; policyToken = configuration.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, localToken, policyToken);
        var ct = linked.Token;
        try { await checkGate.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        var started = clock.Now; var previousRound = round;
        Task<(ClashSnapshot?, string?)>? configTask = null;
        try
        {
            if (!Active(token)) return;
            configTask = ReadSnapshotAsync(ct);
            var probe = await service.ProbeProxyAsync(ct);
            if (!Active(token)) return;
            failures = probe.Healthy ? 0 : failures + 1;
            if (probe.Healthy) Healthy(token, probe);
            else Phase(token, ProxyPhase.Suspect, probe.Failure == ProxyProbeFailure.SlowTimeout
                ? $"当前节点连续两次延迟超过 {ProxyPolicy.MaximumLatencyMs} ms，视为节点超时；暂停 agent 续接并开始恢复"
                : $"代理请求失败；暂停 agent 续接（连续 {failures} 轮）");
            var (snapshot, error) = await configTask;
            if (!Active(token)) return;
            if (snapshot is not null) Apply(token, snapshot);
            else Update(token, s => s with { CatalogueError = error });
            if (probe.Healthy)
            {
                if (error is not null) Update(token, s => s with { Detail = "代理可用；" + error });
                else if (snapshot?.CanSelect == false) Update(token, s => s with { Detail = "代理可用；非全局模式，自动切换暂停" });
                return;
            }
            if (failures < 2 && probe.Failure != ProxyProbeFailure.SlowTimeout) return;
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
        var policy = Preferences;
        if (policy.PriorityRegionIds.Count == 0) { Phase(token, ProxyPhase.Unavailable, "未选择恢复地区；继续检查代理连通性"); return; }
        round++;
        Update(token, s => s with { Round = round, Delays = [], Completed = 0 });
        var expected = initial;
        List<NodeDelay> completedRegions = [];
        // Current region first, then the saved priority from the top. Each region appears once.
        foreach (var regionId in policy.RecoveryOrder(initial.CurrentRegionId))
        {
            if (!Active(token)) return;
            var regionName = RegionResolver.ForId(regionId)!.Name;
            var candidates = expected.Candidates.Where(n => n.Region!.Id == regionId).ToArray();
            Update(token, s => s with { TestingRegionId = regionId, CandidateCount = candidates.Length, Completed = 0 });
            if (candidates.Length == 0) { Emit(token, $"{regionName} 暂无可选节点，本轮跳过"); continue; }
            List<NodeDelay> delays = [];
            var remaining = candidates;
            for (var index = 0; index < ProxyPolicy.TestUrls.Length; index++)
            {
                if (remaining.Length == 0) break;
                Update(token, s => s with { Completed = 0, CandidateCount = remaining.Length });
                Phase(token, ProxyPhase.Testing, $"第 {round} 轮 · {regionName} · {(index == 0 ? "主地址" : "备用地址")}测速");
                (delays, remaining) = await MeasureAsync(remaining, expected.Endpoint, ProxyPolicy.TestUrls[index], token, completedRegions, ct);
                if (!Active(token)) return;
                if (delays.Count > 0) break;
            }
            var ranked = ProxyPolicy.Ranked(delays, candidates.Select(n => n.Name).ToHashSet(StringComparer.Ordinal));
            completedRegions.AddRange(ranked);
            Update(token, s => s with { Delays = completedRegions.ToArray() });
            if (ranked.Length == 0) { Emit(token, $"{regionName} 全部节点超时或不可用，本轮不再尝试该地区"); continue; }
            var fresh = await CheckedSnapshotAsync(expected, token, ct);
            if (fresh is null) return;
            var recovered = await service.ProbeProxyAsync(ct);
            if (!Active(token)) return;
            if (recovered.Healthy) { Healthy(token, recovered); return; }
            expected = fresh;
            foreach (var candidate in ranked)
            {
                if (!Active(token)) return;
                var before = await CheckedSnapshotAsync(expected, token, ct);
                if (before is null) return;
                Phase(token, ProxyPhase.Switching, $"{regionName} · 验证 {candidate.Name} · {candidate.Milliseconds} ms");
                Exception? writeError = null;
                if (before.Selected != candidate.Name)
                {
                    try
                    {
                        Task submission;
                        lock (sync)
                        {
                            if (!running || generation != token || !preferences.Contains(regionId)) return;
                            ct.ThrowIfCancellationRequested();
                            submission = service.SelectAsync(candidate.Name, before, ct);
                        }
                        await submission;
                    }
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
                    Phase(token, ProxyPhase.Retrying, writeError is null ? "节点选择或配置已变化；放弃本轮结果，稍后复核" : "切换未确认；稍后读取实际选择再重试"); return;
                }
                Emit(token, $"已选择 {regionName} 节点：{candidate.Name}（{candidate.Milliseconds} ms）；验证代理连通性");
                var verification = await service.ProbeProxyAsync(ct);
                if (!Active(token)) return;
                var confirmed = await CheckedSnapshotAsync(after, token, ct);
                if (confirmed is null) return;
                if (verification.Healthy)
                {
                    Apply(token, confirmed); Healthy(token, verification);
                    Emit(token, $"代理已恢复：{candidate.Name}；符合条件的 agent 将重新冷却 10 秒"); return;
                }
                Emit(token, verification.Failure == ProxyProbeFailure.SlowTimeout
                    ? $"{candidate.Name} 连续两次延迟超过 {ProxyPolicy.MaximumLatencyMs} ms，视为节点超时，尝试 {regionName} 下一可用节点"
                    : $"{candidate.Name} 实际代理请求失败，尝试 {regionName} 下一可用节点");
                expected = confirmed;
            }
            Emit(token, $"{regionName} 实际连通性均未通过，按优先级尝试其他地区");
        }
        Update(token, s => s with { TestingRegionId = null });
        Phase(token, ProxyPhase.Retrying, "全部已选地区暂不可用；稍后开始新一轮检查和测速");
    }

    private async Task<(List<NodeDelay> Delays, ClashNode[] RequestFailures)> MeasureAsync(ClashNode[] nodes, LocalEndpoint endpoint, string url, int token,
        IReadOnlyList<NodeDelay> previousRegions, CancellationToken ct)
    {
        List<NodeDelay> results = [];
        List<ClashNode> requestFailures = [];
        var allowed = nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        ct.ThrowIfCancellationRequested();
        var tasks = nodes.Select(async node =>
        {
            var probe = await LatencyPolicy.CheckAsync(async cancellation => await service.DelayAsync(node.Name, endpoint, url, cancellation), ct);
            return (Node: node, Probe: probe);
        }).ToList();
        var all = tasks.ToArray();
        try
        {
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks); tasks.Remove(completed);
                var (node, probe) = await completed;
                ct.ThrowIfCancellationRequested();
                if (probe.Healthy) results.Add(new(node.Name, probe.LatencyMs!.Value, node.Region!.Id));
                else if (probe.Failure == ProxyProbeFailure.SlowTimeout)
                    Emit(token, $"{node.Name} 连续两次延迟超过 {ProxyPolicy.MaximumLatencyMs} ms，视为节点超时，本轮排除");
                else requestFailures.Add(node);
                Update(token, s => s with { Completed = s.Completed + 1,
                    Delays = previousRegions.Concat(ProxyPolicy.Ranked(results, allowed)).ToArray() });
            }
        }
        finally { try { await Task.WhenAll(all); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { } }
        return (results, requestFailures.ToArray());
    }

    private async Task<ClashSnapshot?> CheckedSnapshotAsync(ClashSnapshot expected, int token, CancellationToken ct)
    {
        var (fresh, error) = await ReadSnapshotAsync(ct);
        if (!Active(token)) return null;
        if (fresh is null) { Phase(token, ProxyPhase.Unavailable, error ?? "Clash 连接失败"); return null; }
        Apply(token, fresh);
        if (fresh.CanSelect && fresh.HasSameSelectionContext(expected)) return fresh;
        Phase(token, ProxyPhase.Retrying, "节点选择或配置已变化；放弃旧测速结果，稍后复核"); return null;
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
    private static ProxyStatus WithCatalogue(ProxyStatus s, ClashSnapshot snapshot) => s with { Selected = snapshot.Selected,
        CurrentRegionId = snapshot.CurrentRegionId, Regions = snapshot.Regions, UnknownNodeCount = snapshot.Leaves.Length - snapshot.Candidates.Length,
        CandidateCount = s.TestingRegionId is { } id ? snapshot.Candidates.Count(n => n.Region!.Id == id) : snapshot.Candidates.Length,
        CatalogueLoaded = true, CatalogueError = null };
    private void Apply(int token, ClashSnapshot snapshot) => Update(token, s => WithCatalogue(s, snapshot));
    private void Healthy(int token, ProxyProbe probe)
    {
        Update(token, s => s with { HealthyAt = clock.Now, LatencyMs = probe.LatencyMs, TestingRegionId = null });
        Phase(token, ProxyPhase.Healthy, "代理连通；保持当前节点，故障后才切换");
    }
    private void Phase(int token, ProxyPhase phase, string detail)
    {
        Update(token, s => { if (s.Phase == ProxyPhase.Healthy && phase != ProxyPhase.Healthy) Interlocked.Increment(ref epoch); return s with { Phase = phase, Detail = detail, HealthyAt = phase == ProxyPhase.Healthy ? s.HealthyAt : null }; });
        Emit(token, detail);
    }
    private void Emit(int token, string message)
    {
        lock (sync) { if (!running || generation != token || lastEvent == message) return; lastEvent = message; Event?.Invoke(message); }
    }
    public async ValueTask DisposeAsync()
    {
        catalogueLifetime.Cancel(); await StopAsync();
        await preferencesGate.WaitAsync();
        try
        {
            await checkGate.WaitAsync();
            lock (sync) { disposed = true; configuration.Dispose(); }
            checkGate.Release();
        }
        finally { preferencesGate.Release(); }
        catalogueLifetime.Dispose(); checkGate.Dispose(); preferencesGate.Dispose(); wake.Dispose();
    }
}
