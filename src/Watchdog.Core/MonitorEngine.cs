namespace Watchdog.Core;

public sealed class MonitorEngine : IAsyncDisposable
{
    private sealed class Attempt(CancellationTokenSource cancellation)
    {
        private bool disposed;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
        public bool Submitted;
        public void Cancel() { lock (this) { if (!disposed) Cancellation.Cancel(); } }
        public void Dispose() { lock (this) { disposed = true; Cancellation.Dispose(); } }
    }
    private readonly object sync = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1), scanGate = new(1, 1);
    private readonly IHerdrService service;
    private readonly IEventSink? sink;
    private readonly MonotonicClock clock;
    private readonly TimeSpan pollInterval;
    private readonly RecoveryMachine machine = new();
    private readonly Dictionary<string, Attempt> attempts = [];
    private readonly Dictionary<string, string> rowDetails = [];
    private readonly List<MonitorEvent> events = [];
    private AgentRow[] rows = [];
    private bool running, automaticLoop = true;
    private bool proxyRecoveryEnabled = true, agentRecoveryEnabled = true;
    private bool CanResume => agentRecoveryEnabled && (!proxyRecoveryEnabled || Proxy.PermitsAgent);
    private int generation;
    private string connection = "服务已停止";
    private string? lastConnectionError;
    private DateTimeOffset? lastScan;
    private CancellationTokenSource? lifetime;
    private Task? loop;
    public ProxyMonitor Proxy { get; }
    public int CooldownSeconds { get { lock (sync) return machine.CooldownSeconds; } }
    public void SetCooldownSeconds(int seconds)
    {
        InterventionCooldown.Validate(seconds);
        // Proxy notifications acquire sync; do not hold the engine lock across this call.
        Proxy.SetCooldownSeconds(seconds);
        lock (sync)
        {
            machine.SetCooldownSeconds(seconds);
            rows = rows.Select(r => r.Deadline is null ? r : r with { Deadline = machine.Deadline(r.Id) is { } deadline ? Math.Max(clock.Now, deadline) : null }).ToArray();
            Publish();
        }
    }
    public async Task ApplyAppSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        InterventionCooldown.Validate(settings.CooldownSeconds);
        await lifecycle.WaitAsync(ct);
        try
        {
            SetCooldownSeconds(settings.CooldownSeconds);
            Attempt[] cancelled;
            bool startProxy;
            lock (sync)
            {
                var changed = proxyRecoveryEnabled != settings.ProxyRecoveryEnabled || agentRecoveryEnabled != settings.AgentRecoveryEnabled;
                proxyRecoveryEnabled = settings.ProxyRecoveryEnabled; agentRecoveryEnabled = settings.AgentRecoveryEnabled;
                cancelled = changed ? attempts.Values.Where(a => !a.Submitted).ToArray() : [];
                if (changed)
                {
                    foreach (var attempt in cancelled) attempt.Cancel();
                    machine.Stop();
                    rows = rows.Select(r => r with { Deadline = null }).ToArray();
                    Append("设置", $"Clash 自动恢复：{(proxyRecoveryEnabled ? "开启" : "关闭")}；agent 自动续接：{(agentRecoveryEnabled ? "开启" : "关闭")}");
                }
                rows = rows.Select(ApplyRowPolicy).ToArray();
                startProxy = running && proxyRecoveryEnabled;
                Publish();
            }
            await Proxy.SetRecoveryEnabledAsync(settings.ProxyRecoveryEnabled);
            if (!startProxy) await Proxy.StopAsync();
            else if (!Proxy.Running) Proxy.Start(automaticLoop);
            await Task.WhenAll(cancelled.Select(a => a.Task));
        }
        finally { lifecycle.Release(); }
    }
    private AgentRow ApplyRowPolicy(AgentRow row) => !agentRecoveryEnabled
        ? row with { Deadline = null, Detail = row.Detail.StartsWith("自动续接已关闭", StringComparison.Ordinal) ? row.Detail : "自动续接已关闭 · " + row.Detail }
        : row with { Detail = row.Detail.StartsWith("自动续接已关闭 · ", StringComparison.Ordinal) ? row.Detail["自动续接已关闭 · ".Length..] : row.Detail };
    public event Action<MonitorSnapshot>? SnapshotChanged;
    public MonitorSnapshot Snapshot { get { lock (sync) return BuildSnapshot(); } }

    public MonitorEngine(IHerdrService service, IClashService proxyService, IEventSink? sink = null,
        TimeProvider? timeProvider = null, TimeSpan? pollInterval = null, TimeSpan? proxyInterval = null)
    {
        this.service = service; this.sink = sink; clock = new(timeProvider); this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        Proxy = new(proxyService, clock, proxyInterval);
        Proxy.StatusChanged += status =>
        {
            lock (sync)
            {
                if (proxyRecoveryEnabled && !status.PermitsAgent(clock.Now))
                {
                    machine.Stop();
                    foreach (var attempt in attempts.Values.Where(a => !a.Submitted)) attempt.Cancel();
                    rows = rows.Select(r => r.Deadline is null ? r : r with { Deadline = null, Detail = "等待代理恢复" }).ToArray();
                }
                Publish();
            }
        };
        Proxy.Event += message => { lock (sync) { if (running) { Append("Clash", message); Publish(); } } };
    }

    public async Task StartAsync(bool automatic = true, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            lock (sync)
            {
                if (running) return;
                running = true; automaticLoop = automatic; generation++; lifetime = new(); lastConnectionError = null;
                connection = "正在连接 Herdr…"; Append("服务", $"开始监控；首次立即介入，后续独立冷却 {machine.CooldownSeconds} 秒"); Publish();
            }
            if (proxyRecoveryEnabled) Proxy.Start(automatic);
            if (automatic)
            {
                var ct = lifetime.Token;
                loop = Task.Run(async () =>
                {
                    try { while (!ct.IsCancellationRequested) { await ScanNowAsync(ct); await clock.Delay(pollInterval, ct); } }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                }, CancellationToken.None);
            }
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            Task? pendingLoop;
            Attempt[] pending;
            CancellationTokenSource? source;
            lock (sync)
            {
                if (!running) return;
                running = false; generation++; source = lifetime; lifetime = null;
                pendingLoop = loop; loop = null; pending = attempts.Values.ToArray();
                machine.Stop(); connection = "服务已停止";
                rows = rows.Select(r => r with { Deadline = null, Detail = "监控已停止" }).ToArray(); rowDetails.Clear();
                Append("服务", "已停止；取消全部待发送任务"); Publish();
            }
            source?.Cancel();
            foreach (var attempt in pending) attempt.Cancel();
            await Proxy.StopAsync();
            if (pendingLoop is not null) await pendingLoop;
            await Task.WhenAll(pending.Select(a => a.Task));
            await scanGate.WaitAsync(); scanGate.Release();
            source?.Dispose();
        }
        finally { lifecycle.Release(); }
    }

    public async Task ScanNowAsync(CancellationToken cancellationToken = default)
    {
        int token; CancellationToken localToken;
        lock (sync) { if (!running || lifetime is null) return; token = generation; localToken = lifetime.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, localToken);
        var ct = linked.Token;
        if (!await scanGate.WaitAsync(0, ct)) return;
        try
        {
            var sessions = await service.SessionsAsync(ct);
            var results = await Task.WhenAll(sessions.Select(async session =>
            {
                try
                {
                    var agents = await service.AgentsAsync(session, ct);
                    List<Observation> observed = [];
                    foreach (var chunk in agents.Chunk(8))
                        observed.AddRange(await Task.WhenAll(chunk.Select(async agent =>
                        {
                            try { return await service.ObserveAsync(agent, session, false, ct); }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception e) { return new Observation(session, agent, ForegroundPid: null, Error: e.Message); }
                        })));
                    return (Session: session, Observed: observed.ToArray(), Error: (string?)null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { return (Session: session, Observed: Array.Empty<Observation>(), Error: (string?)e.Message); }
            }));
            lock (sync)
            {
                if (!Active(token) || ct.IsCancellationRequested) return;
                var failed = results.Where(r => r.Error is not null).Select(r => r.Session.Endpoint.Key).ToHashSet(StringComparer.Ordinal);
                var observed = results.SelectMany(r => r.Observed).ToArray();
                foreach (var error in results.Where(r => r.Error is not null))
                    if (lastConnectionError != error.Error) { Append("连接", error.Error!); lastConnectionError = error.Error; }
                connection = sessions.Count == 0 ? "等待 Herdr 启动…" : $"已连接 {sessions.Count - failed.Count} 个会话 · {observed.Length} 个 agent";
                if (failed.Count > 0) connection += " · 部分连接异常，正在重试"; else lastConnectionError = null;
                lastScan = clock.Provider.GetUtcNow();
                List<AgentRow> next = [];
                List<ResumeIntent> intents = [];
                if (!CanResume) machine.Stop();
                foreach (var observation in observed)
                {
                    var (row, intent) = machine.Evaluate(observation, clock.Now);
                    if (!CanResume)
                    {
                        machine.Suspend([observation.Key]); intent = null;
                        row = row with { Deadline = null, Detail = agentRecoveryEnabled ? "等待代理恢复" : row.Detail };
                    }
                    row = ApplyRowPolicy(row);
                    next.Add(row);
                    if (rowDetails.GetValueOrDefault(row.Id) != row.Detail) { Append(row.Location, row.Detail); rowDetails[row.Id] = row.Detail; }
                    if (intent is not null) intents.Add(intent);
                }
                next.AddRange(rows.Where(r => failed.Any(f => r.Id.StartsWith(f + "|", StringComparison.Ordinal)))
                    .Select(r => r with { Deadline = null, Detail = "会话连接异常，暂停发送" }));
                machine.Suspend(rows.Select(r => r.Id).Except(observed.Select(o => o.Key)));
                rows = next.OrderBy(r => r.Location, StringComparer.Ordinal).ToArray();
                foreach (var intent in intents)
                {
                    var key = intent.Observation.Key;
                    if (attempts.ContainsKey(key)) continue;
                    var pending = new Attempt(CancellationTokenSource.CreateLinkedTokenSource(localToken));
                    attempts[key] = pending;
                    var epoch = Proxy.HealthEpoch;
                    pending.Task = Task.Run(() => AttemptAsync(intent, pending, token, epoch), CancellationToken.None);
                }
                Publish();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            lock (sync)
            {
                if (!Active(token)) return;
                connection = e.Message; machine.Stop();
                rows = rows.Select(r => r with { Deadline = null, Detail = "连接异常，暂停发送" }).ToArray();
                if (lastConnectionError != connection) { Append("连接", connection); lastConnectionError = connection; }
                Publish();
            }
        }
        finally { scanGate.Release(); }
    }

    private async Task AttemptAsync(ResumeIntent intent, Attempt attempt, int token, long proxyEpoch)
    {
        var original = intent.Observation; var ct = attempt.Cancellation.Token;
        var target = $"{original.Session.Name} / {original.Agent.PaneId}";
        try
        {
            var fresh = await service.ObserveAsync(original.Agent, original.Session, true, ct);
            Task submission;
            lock (sync)
            {
                if (!Active(token) || ct.IsCancellationRequested || !CanResume || proxyRecoveryEnabled && Proxy.HealthEpoch != proxyEpoch) return;
                if (!machine.Claim(intent, fresh, clock.Now)) { Append(target, "已取消本次发送：状态、输入或 agent 身份发生变化"); return; }
                attempt.Submitted = true;
                try { submission = service.ResumeAsync(fresh, ct); }
                catch (Exception e) { submission = Task.FromException(e); }
                Append(target, "发送「继续」并回车；依据：" + intent.Evidence.Message[..Math.Min(300, intent.Evidence.Message.Length)]);
            }
            try
            {
                await submission;
                lock (sync) { if (!Active(token)) return; machine.SubmissionConfirmed(fresh.Key); Append(target, "Herdr 已确认提交并观察到 agent 开始运行"); }
            }
            catch (Exception e)
            {
                lock (sync)
                {
                    if (!Active(token)) return;
                    machine.SubmissionFailed(fresh.Key, e is HerdrException { DefinitelyNotSent: true }, clock.Now);
                    Append(target, "提交异常：" + e.Message);
                }
            }
            lock (sync)
            {
                if (!Active(token)) return;
                var row = machine.Evaluate(fresh, clock.Now).Row;
                if (!CanResume) { machine.Suspend([fresh.Key]); row = row with { Deadline = null }; }
                row = ApplyRowPolicy(row);
                rows = rows.Select(r => r.Id == row.Id ? row : r).ToArray();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            lock (sync) { if (Active(token)) { machine.Suspend([original.Key]); Append(target, "发送前复核失败，已取消：" + e.Message); } }
        }
        finally
        {
            lock (sync)
            {
                if (attempts.GetValueOrDefault(original.Key) == attempt) attempts.Remove(original.Key);
                Publish();
                attempt.Dispose();
            }
        }
    }

    private bool Active(int token) => running && generation == token;
    private void Append(string target, string message)
    {
        var value = new MonitorEvent(clock.Provider.GetUtcNow(), target, message);
        events.Add(value); if (events.Count > 500) events.RemoveRange(0, events.Count - 500);
        sink?.Append(value);
    }
    private MonitorSnapshot BuildSnapshot() => new(running, connection, lastScan, rows.ToArray(), events.ToArray(), Proxy.Status, proxyRecoveryEnabled, agentRecoveryEnabled);
    private void Publish() => SnapshotChanged?.Invoke(BuildSnapshot());
    public async ValueTask DisposeAsync() { await StopAsync(); await Proxy.DisposeAsync(); lifecycle.Dispose(); scanGate.Dispose(); }
}
