namespace Watchdog.Core;

public sealed class ProxyMonitor(IClashService service, MonotonicClock? clock = null, TimeSpan? interval = null,
    ProxyRegionPreferences? preferences = null) : IAsyncDisposable
{
    private readonly MonotonicClock clock = clock ?? new();
    private readonly TimeSpan interval = interval ?? ProxyPolicy.HeartbeatInterval;
    private readonly object sync = new();
    private readonly SemaphoreSlim checkGate = new(1, 1), preferencesGate = new(1, 1), wake = new(0, 1);
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private bool runRequested, automaticLoop = true, configuring, disposing;
    private Task stopping = Task.CompletedTask;
    private Task? disposal;
    private readonly CancellationTokenSource catalogueLifetime = new();
    private CancellationTokenSource configuration = new();
    private ProxyRegionPreferences preferences = preferences ?? ProxyRegionPreferences.Default;
    private ProxyStatus status = new() { RegionPriority = (preferences ?? ProxyRegionPreferences.Default).PriorityRegionIds };
    private volatile bool running;
    private bool recoveryEnabled = true;
    public async Task SetRecoveryEnabledAsync(bool enabled)
    {
        lock (sync) recoveryEnabled = enabled;
        if (!enabled) await StopAsync();
    }
    private bool disposed;
    private long epoch;
    private int generation, round;
    private int consecutiveFailures, recoveryAttempt;
    private bool recoveryPending;
    private string? failedNode;
    private ClashSnapshot? probeContext;
    private int cooldownSeconds = 5;
    private double lastSwitch = double.NegativeInfinity;
    private TaskCompletionSource cooldownChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void SetCooldownSeconds(int seconds)
    {
        InterventionCooldown.Validate(seconds);
        lock (sync)
        {
            cooldownSeconds = seconds;
            var previous = cooldownChanged;
            cooldownChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }
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
            ObjectDisposedException.ThrowIf(disposed || disposing, this);
            if (!recoveryEnabled) return;
            runRequested = true;
            automaticLoop = automatic;
            StartRequested();
        }
    }

    // Called under sync. Configuration and draining must finish before a new loop starts.
    private void StartRequested()
    {
        if (!runRequested || running || !recoveryEnabled || configuring || disposing || disposed || !stopping.IsCompletedSuccessfully) return;
        running = true; generation++; Interlocked.Increment(ref epoch); round = 0;
        consecutiveFailures = recoveryAttempt = 0; recoveryPending = false; failedNode = null;
        while (wake.Wait(0)) { }
        lifetime = new();
        status = status with { Phase = ProxyPhase.Checking, HealthyAt = null, NextCheck = null, SwitchDeadline = null, Round = 0,
            Delays = [], TestingRegionId = null, Completed = 0, RegionPriority = preferences.PriorityRegionIds,
            ConnectionSeconds = 0, ConsecutiveFailures = 0, DisconnectReason = null, ValidatingCandidate = false };
        Phase(generation, ProxyPhase.Checking, "正在经 127.0.0.1:7890 建立 Codex 长连接");
        if (automaticLoop)
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

    public Task StopAsync() => StopCore(clearIntent: true);

    private Task StopCore(bool clearIntent)
    {
        Task? pending; CancellationTokenSource? source;
        TaskCompletionSource completion;
        lock (sync)
        {
            if (clearIntent) runRequested = false;
            if (!running) return stopping;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopping = completion.Task;
            running = false; generation++; Interlocked.Increment(ref epoch);
            source = lifetime; lifetime = null; pending = loop; loop = null;
            status = status with { Phase = ProxyPhase.Stopped, HealthyAt = null, NextCheck = null, SwitchDeadline = null,
                TestingRegionId = null, ConnectionSeconds = 0, ValidatingCandidate = false, Detail = "代理监控已停止；保留当前节点" };
            StatusChanged?.Invoke(status);
        }
        _ = DrainStopAsync(pending, source, completion);
        return completion.Task;
    }

    private async Task DrainStopAsync(Task? pending, CancellationTokenSource? source, TaskCompletionSource completion)
    {
        try
        {
            source?.Cancel();
            if (pending is not null) await pending;
            await checkGate.WaitAsync();
            try { await service.ResetProbeConnectionsAsync(CancellationToken.None); probeContext = null; }
            finally { checkGate.Release(); }
            source?.Dispose();
            lock (sync)
            {
                stopping = Task.CompletedTask;
                StartRequested();
                completion.SetResult();
            }
        }
        catch (Exception e) { source?.Dispose(); completion.TrySetException(e); }
    }

    public async Task ConfigureAsync(ProxyRegionPreferences value, Func<Task> save, CancellationToken ct, bool restore = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, catalogueLifetime.Token);
        ct = linked.Token;
        await configurationGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            await ConfigureCoreAsync(value, save, ct, restore);
        }
        finally
        {
            lock (sync) { configuring = false; StartRequested(); }
            configurationGate.Release();
        }
    }

    private async Task ConfigureCoreAsync(ProxyRegionPreferences value, Func<Task> save, CancellationToken ct, bool restore)
    {
        var previous = Preferences;
        var sourcesChanged = previous.SelectedSourceIds is null ? value.SelectedSourceIds is not null :
            value.SelectedSourceIds is null || !previous.SelectedSourceIds.SequenceEqual(value.SelectedSourceIds);
        if (!sourcesChanged && !restore)
        {
            await save();
            await ApplyPreferencesAsync(value, CancellationToken.None);
            return;
        }
        lock (sync) configuring = true;
        await StopCore(clearIntent: false);
        ClashSnapshot? before = null;
        try
        {
            if (restore) { before = await service.SnapshotAsync(ct); await service.RestoreSourcesAsync(ct); }
            else if (sourcesChanged) await service.PrepareSourcesAsync(value, ct);
            ct.ThrowIfCancellationRequested();
            await save();
            await ApplyPreferencesAsync(value, CancellationToken.None);
        }
        catch (Exception failure)
        {
            try
            {
                if (sourcesChanged || restore) await service.PrepareSourcesAsync(previous, CancellationToken.None);
                if (before?.Selected is { } selected)
                {
                    var after = await service.SnapshotAsync(CancellationToken.None);
                    if (after.Candidates.Any(n => n.Name == selected)) await service.SelectAsync(selected, after, CancellationToken.None);
                }
            }
            catch (Exception rollback) { throw new IOException("设置未保存，运行配置回滚失败，请检查 Clash 当前状态", new AggregateException(failure, rollback)); }
            throw;
        }
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
            await checkGate.WaitAsync();
            try
            {
                await service.ResetProbeConnectionsAsync(CancellationToken.None); probeContext = null;
                consecutiveFailures = recoveryAttempt = 0; recoveryPending = false; failedNode = null;
                Update(generation, s => s with { ConnectionSeconds = 0, ConsecutiveFailures = 0, HealthyAt = null, ValidatingCandidate = false });
            }
            finally { checkGate.Release(); previous.Dispose(); }
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
            await BindContextAsync(snapshot, generation, ct);
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
            if (!running || !recoveryEnabled || lifetime is null) return;
            token = generation; localToken = lifetime.Token; policyToken = configuration.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, localToken, policyToken);
        var ct = linked.Token;
        try { await checkGate.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        var nextDelay = interval.TotalSeconds;
        try
        {
            if (!Active(token)) return;
            string? preparationError = null;
            var reloaded = false;
            try { await service.PrepareSourcesAsync(Preferences, ct, () => { reloaded = true; Phase(token, ProxyPhase.Checking, "重新加载订阅聚合配置"); }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { preparationError = e.Message; }
            if (reloaded) await ResetConnectionAsync(ct);
            var (snapshot, error) = await ReadSnapshotAsync(ct);
            if (!Active(token)) return;
            await BindContextAsync(snapshot, token, ct);
            if (snapshot is not null) Apply(token, snapshot);
            else Update(token, s => s with { CatalogueError = error });
            // Monitoring must keep recovering on its own when switching is unavailable.
            if (recoveryPending && (snapshot?.CanSelect != true ||
                !snapshot.Candidates.Any(n => Preferences.Contains(n.Region?.Id) && Preferences.Allows(n, snapshot.CurrentSourceId))))
                recoveryPending = false;
            if (!recoveryPending)
            {
                var probe = await service.ProbeProxyAsync(ct);
                if (!Active(token)) return;
                if (snapshot is not null)
                {
                    var (after, _) = await ReadSnapshotAsync(ct);
                    if (after is null || !snapshot.HasSameSelectionContext(after))
                    {
                        await BindContextAsync(after, token, ct);
                        Phase(token, ProxyPhase.Checking, "节点或控制接口已变化，重新建立长连接");
                        return;
                    }
                }
                UpdateConnection(token, probe);
                if (probe.Healthy)
                {
                    if (probe.ConnectionSeconds >= ProxyPolicy.MaintenanceSeconds) consecutiveFailures = 0;
                    Healthy(token, probe);
                    if (preparationError is not null || error is not null)
                        Update(token, s => s with { Detail = "Codex 长连接可用；" + (preparationError ?? error) });
                    return;
                }
                await service.ResetProbeConnectionsAsync(ct);
                consecutiveFailures = probe.ConnectionSeconds >= ProxyPolicy.MaintenanceSeconds ? 0 : Math.Min(2, consecutiveFailures + 1);
                Update(token, s => s with { ConsecutiveFailures = consecutiveFailures });
                Phase(token, ProxyPhase.Suspect, $"Codex 长连接断开（未满 60 秒失败 {consecutiveFailures}/2）：{probe.Detail ?? "连接失败"}");
                if (consecutiveFailures < 2) { nextDelay = 1; return; }
                recoveryPending = true; failedNode = snapshot?.Selected; round = recoveryAttempt = 0;
            }
            if (preparationError is not null) { Phase(token, ProxyPhase.Unavailable, "订阅准备失败：" + preparationError); }
            else if (snapshot is null) { Phase(token, ProxyPhase.Unavailable, error ?? "等待 Clash 控制接口恢复"); }
            else if (!snapshot.CanSelect) { Phase(token, ProxyPhase.Unavailable, "当前不是全局 GLOBAL 选择模式，暂停自动切换"); }
            else await RecoverAsync(snapshot, token, ct);
            if (recoveryPending) nextDelay = Math.Min(30, Math.Pow(2, Math.Min(5, ++recoveryAttempt)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await service.ResetProbeConnectionsAsync(CancellationToken.None);
            Update(token, s => s with { HealthyAt = null, ConnectionSeconds = 0, ValidatingCandidate = false });
        }
        catch (Exception e) { Phase(token, ProxyPhase.Unavailable, "代理检查失败：" + e.Message); }
        finally
        {
            Update(token, s => s with { NextCheck = clock.Now + nextDelay });
            checkGate.Release();
        }
    }

    private async Task ResetConnectionAsync(CancellationToken ct)
    {
        await service.ResetProbeConnectionsAsync(ct); probeContext = null;
        consecutiveFailures = recoveryAttempt = 0; recoveryPending = false; failedNode = null;
    }
    private async Task BindContextAsync(ClashSnapshot? snapshot, int token, CancellationToken ct)
    {
        if (probeContext is not null && (snapshot is null || !probeContext.HasSameSelectionContext(snapshot)))
        {
            await ResetConnectionAsync(ct);
            Update(token, s => s with { HealthyAt = null, ConnectionSeconds = 0, ConsecutiveFailures = 0, ValidatingCandidate = false });
        }
        probeContext = snapshot;
    }
    private void UpdateConnection(int token, ProxyProbe probe)
    {
        Update(token, s => s with { ConnectionSeconds = probe.ConnectionSeconds,
            HealthyAt = probe.Healthy ? s.HealthyAt : null,
            DisconnectReason = probe.Healthy ? s.DisconnectReason : probe.Detail ?? "连接失败" });
        if (!probe.Healthy) Emit(token, "Codex 长连接断开：" + (probe.Detail ?? "连接失败"));
    }
    private async Task<ProxyProbe> VerifyCandidateAsync(ClashSnapshot expected, int token, CancellationToken ct)
    {
        Update(token, s => s with { ValidatingCandidate = true, ConnectionSeconds = 0 });
        Phase(token, ProxyPhase.Switching, "候选节点需连续保持 10 秒 Codex 长连接");
        await foreach (var probe in service.ObserveProxyConnectionAsync(clock.Provider, ct))
        {
            if (!Active(token)) return new(false);
            if (await CheckedSnapshotAsync(expected, token, ct) is null) return new(false);
            UpdateConnection(token, probe);
            if (!probe.Healthy || probe.ConnectionSeconds >= ProxyPolicy.CandidateSeconds) return probe;
        }
        return new(false);
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
            var candidates = expected.Candidates.Where(n => n.Region!.Id == regionId && policy.Allows(n, expected.CurrentSourceId) &&
                (round > 1 || n.Name != failedNode)).ToArray();
            Update(token, s => s with { TestingRegionId = regionId, CandidateCount = candidates.Length, Completed = 0, ValidatingCandidate = false });
            if (candidates.Length == 0) { Emit(token, $"{regionName} 暂无可选节点，本轮跳过"); continue; }
            Phase(token, ProxyPhase.Testing, $"第 {round} 轮 · {regionName} · Codex 节点测速（≤800 ms）");
            var delays = await MeasureAsync(candidates, expected.Endpoint, token, completedRegions, ct);
            if (!Active(token)) return;
            var ranked = ProxyPolicy.Ranked(delays, candidates.Select(n => n.Name).ToHashSet(StringComparer.Ordinal));
            completedRegions.AddRange(ranked);
            Update(token, s => s with { Delays = completedRegions.ToArray() });
            if (ranked.Length == 0) { Emit(token, $"{regionName} 全部节点超时或不可用，本轮不再尝试该地区"); continue; }
            var fresh = await CheckedSnapshotAsync(expected, token, ct);
            if (fresh is null) return;
            expected = fresh;
            foreach (var candidate in ranked)
            {
                if (!Active(token)) return;
                var before = await CheckedSnapshotAsync(expected, token, ct);
                if (before is null) return;
                Exception? writeError = null;
                if (before.Selected != candidate.Name)
                {
                    while (true)
                    {
                        await WaitForSwitchAsync(token, ct);
                        if (!Active(token)) return;
                        before = await CheckedSnapshotAsync(expected, token, ct);
                        if (before is null) return;
                        var health = await service.ProbeProxyAsync(ct);
                        if (!Active(token)) return;
                        if (health.Healthy && health.ConnectionSeconds >= ProxyPolicy.MaintenanceSeconds)
                        { consecutiveFailures = 0; Healthy(token, health); return; }
                        before = await CheckedSnapshotAsync(expected, token, ct);
                        if (before is null) return;
                        await service.ResetProbeConnectionsAsync(ct);
                        Task? submission = null;
                        try
                        {
                            lock (sync)
                            {
                                if (!running || !recoveryEnabled || generation != token || !preferences.Contains(regionId)) return;
                                ct.ThrowIfCancellationRequested();
                                if (clock.Now < lastSwitch + cooldownSeconds) continue;
                                Phase(token, ProxyPhase.Switching, $"{regionName} · 验证 {Display(candidate)} · {candidate.Milliseconds} ms");
                                // Notifications may apply a new interval; check again before issuing the write.
                                if (clock.Now < lastSwitch + cooldownSeconds) continue;
                                lastSwitch = clock.Now;
                                submission = service.SelectAsync(candidate.Name, before, ct);
                            }
                            await submission;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception e) { writeError = e; }
                        break;
                    }
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
                Emit(token, $"已选择 {regionName} 节点：{Display(candidate)}（{candidate.Milliseconds} ms）；验证代理连通性");
                await service.ResetProbeConnectionsAsync(ct);
                probeContext = after;
                var verification = await VerifyCandidateAsync(after, token, ct);
                if (!Active(token)) return;
                var confirmed = await CheckedSnapshotAsync(after, token, ct);
                if (confirmed is null) return;
                if (verification.Healthy && verification.ConnectionSeconds >= ProxyPolicy.CandidateSeconds)
                {
                    Apply(token, confirmed); Healthy(token, verification);
                    Emit(token, $"代理已恢复：{Display(candidate)}；符合条件的 agent 按上次介入时间复核续接"); return;
                }
                await service.ResetProbeConnectionsAsync(ct);
                Emit(token, $"{Display(candidate)} 未保持满 10 秒长连接，尝试 {regionName} 下一可用节点");
                expected = confirmed;
            }
            Emit(token, $"{regionName} 实际连通性均未通过，按优先级尝试其他地区");
        }
        Update(token, s => s with { TestingRegionId = null, ValidatingCandidate = false });
        Phase(token, ProxyPhase.Retrying, "全部已选地区暂不可用；稍后开始新一轮检查和测速");
    }

    private async Task WaitForSwitchAsync(int token, CancellationToken ct)
    {
        while (true)
        {
            Task changed; double deadline;
            lock (sync)
            {
                ct.ThrowIfCancellationRequested();
                if (!running || generation != token) return;
                deadline = lastSwitch + cooldownSeconds;
                if (clock.Now >= deadline) { Update(token, s => s with { SwitchDeadline = null }); return; }
                changed = cooldownChanged.Task;
                Update(token, s => s with { SwitchDeadline = deadline });
                Phase(token, ProxyPhase.CoolingDown, "等待距上次节点切换的冷却间隔");
            }
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = clock.Delay(TimeSpan.FromSeconds(Math.Max(0, deadline - clock.Now)), waiting.Token);
            await Task.WhenAny(delay, changed);
            waiting.Cancel();
            try { await delay; } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            ct.ThrowIfCancellationRequested();
        }
    }

    private static string Display(NodeDelay node) => node.SourceName is { } source ? source + " · " + (node.OriginalName ?? node.Name) : node.Name;
    private async Task<List<NodeDelay>> MeasureAsync(ClashNode[] nodes, LocalEndpoint endpoint, int token,
        IReadOnlyList<NodeDelay> previousRegions, CancellationToken ct)
    {
        List<NodeDelay> results = [];
        var allowed = nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        var tasks = nodes.Select(async node =>
        {
            try { return (Node: node, Delay: await service.DelayAsync(node.Name, endpoint, ProxyPolicy.TestUrl, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return (Node: node, Delay: 0); }
        }).ToList();
        var all = tasks.ToArray();
        try
        {
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks); tasks.Remove(completed);
                var (node, delay) = await completed;
                ct.ThrowIfCancellationRequested();
                if (delay is > 0 and <= ProxyPolicy.MaximumLatencyMs)
                    results.Add(new(node.Name, delay, node.Region!.Id, node.SourceName, node.OriginalName));
                else Emit(token, $"{node.Name} 测速失败或超过 800 ms，本轮排除，不复测");
                Update(token, s => s with { Completed = s.Completed + 1,
                    Delays = previousRegions.Concat(ProxyPolicy.Ranked(results, allowed)).ToArray() });
            }
        }
        finally { try { await Task.WhenAll(all); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { } }
        return results;
    }

    private async Task<ClashSnapshot?> CheckedSnapshotAsync(ClashSnapshot expected, int token, CancellationToken ct)
    {
        var (fresh, error) = await ReadSnapshotAsync(ct);
        if (!Active(token)) return null;
        if (fresh is null) { Phase(token, ProxyPhase.Unavailable, error ?? "Clash 连接失败"); return null; }
        Apply(token, fresh);
        if (fresh.CanSelect && fresh.HasSameSelectionContext(expected)) return fresh;
        await BindContextAsync(fresh, token, ct);
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
        Sources = snapshot.Sources, CurrentSourceId = snapshot.CurrentSourceId, CurrentRegionId = snapshot.CurrentRegionId, Regions = snapshot.Regions, UnknownNodeCount = snapshot.Leaves.Length - snapshot.Candidates.Length,
        CandidateCount = s.TestingRegionId is { } id ? snapshot.Candidates.Count(n => n.Region!.Id == id) : snapshot.Candidates.Length,
        CatalogueLoaded = true, CatalogueError = null };
    private void Apply(int token, ClashSnapshot snapshot) => Update(token, s => WithCatalogue(s, snapshot));
    private void Healthy(int token, ProxyProbe probe)
    {
        if (Status.ValidatingCandidate) consecutiveFailures = 0;
        recoveryPending = false; recoveryAttempt = 0;
        Update(token, s => s with { HealthyAt = probe.HeartbeatAt ?? clock.Now, LatencyMs = probe.LatencyMs, TestingRegionId = null,
            ConnectionSeconds = probe.ConnectionSeconds, ConsecutiveFailures = consecutiveFailures, ValidatingCandidate = false });
        Phase(token, ProxyPhase.Healthy, "Codex 长连接可用；连续两次未满 60 秒断线才更换节点");
    }
    private void Phase(int token, ProxyPhase phase, string detail)
    {
        Update(token, s =>
        {
            if (s.Phase == ProxyPhase.Healthy && phase != ProxyPhase.Healthy) Interlocked.Increment(ref epoch);
            return s with { Phase = phase, Detail = detail, HealthyAt = phase == ProxyPhase.Healthy ? s.HealthyAt : null,
                ValidatingCandidate = phase == ProxyPhase.Switching && s.ValidatingCandidate };
        });
        Emit(token, detail);
    }
    private void Emit(int token, string message)
    {
        lock (sync) { if (!running || generation != token || lastEvent == message) return; lastEvent = message; Event?.Invoke(message); }
    }
    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            disposing = true;
            runRequested = false;
            return new(disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        catalogueLifetime.Cancel(); await StopAsync();
        await configurationGate.WaitAsync();
        await preferencesGate.WaitAsync();
        try
        {
            await checkGate.WaitAsync();
            lock (sync) { disposed = true; configuration.Dispose(); }
            checkGate.Release();
        }
        finally { preferencesGate.Release(); }
        configurationGate.Release();
        catalogueLifetime.Dispose(); checkGate.Dispose(); preferencesGate.Dispose(); configurationGate.Dispose(); wake.Dispose();
    }
}
