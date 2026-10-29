using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using Watchdog.Core;
using Watchdog.Infrastructure;

namespace HerdrAutoContinue;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class AgentRowViewModel(AgentRow row) : ObservableModel
{
    public AgentRow Row { get; private set; } = row;
    public string Title => Row.Title;
    public string Location => Row.Kind + " · " + Row.Location;
    public string State => Row.State switch { "working" => "运行中", "idle" => "空闲", "done" => "已完成", "blocked" => "待确认", _ => "未知" };
    public string Detail => Row.Detail;
    public string Attempts => Row.Attempts.ToString();
    public string Countdown => Row.Deadline is { } deadline ? $"{Math.Max(0, (int)Math.Ceiling(deadline - new MonotonicClock().Now))} 秒后复核" : "";
    public void Update(AgentRow value) { Row = value; Changed(""); }
    public void Tick() => Changed(nameof(Countdown));
}

public sealed class MainViewModel : ObservableModel, IAsyncDisposable
{
    private readonly MonitorEngine engine;
    private readonly DispatcherTimer timer;
    private MonitorSnapshot snapshot;
    private bool busy;
    private string? uiError;
    private readonly RegionSettingsController regionSettings;
    private readonly CancellationTokenSource lifetime = new();
    private Task? initialization, disposal;
    private bool initialized;
    public string CooldownDescription => $"独立冷却 {engine.CooldownSeconds} 秒 · 首次立即介入";
    public void SetCooldownSeconds(int seconds) { engine.SetCooldownSeconds(seconds); Changed(nameof(CooldownDescription)); }
    public bool ProxyRecoveryEnabled => engine.Snapshot.ProxyRecoveryEnabled;
    public bool AgentRecoveryEnabled => engine.Snapshot.AgentRecoveryEnabled;
    public string FeatureSummary => $"Clash 自动恢复{(ProxyRecoveryEnabled ? "已开启" : "已关闭")} · agent 自动续接{(AgentRecoveryEnabled ? "已开启" : "已关闭")}";
    public async Task ApplyAppSettingsAsync(AppSettings settings)
    {
        await engine.ApplyAppSettingsAsync(settings);
        Changed("");
    }
    public string LogPath { get; }
    public ICommand ToggleCommand { get; }
#if DEBUG
    internal MonitorSnapshot QaSnapshot => engine.Snapshot;
#endif
    public ObservableCollection<AgentRowViewModel> Rows { get; } = [];
    public ObservableCollection<MonitorEvent> Events { get; } = [];
    public ObservableCollection<string> Delays { get; } = [];
    public bool Running => snapshot.Running;
    public bool CanToggle => initialized && !busy;
    public ProxyStatus RegionStatus => engine.Proxy.Status;
    public ProxyRegionPreferences RegionPreferences => engine.Proxy.Preferences;
    public string RegionSummary => (RegionStatus.CatalogueLoaded
        ? $"发现 {RegionStatus.Regions.Count} 个地区 · {RegionStatus.Regions.Sum(r => r.NodeCount)} 个节点 · 未识别 {RegionStatus.UnknownNodeCount} 个"
        : "尚未读取地区列表") + (RegionStatus.CatalogueError is { } error ? " · 刷新失败：" + error : RegionStatus.CatalogueLoaded ? "" : " · 正在加载…");
    public string SourceSummary => "已选 " + (RegionPreferences.SelectedSourceIds?.Count ?? (RegionStatus.CurrentSourceId is null ? 0 : 1)) + " 个订阅源";
    public string RegionOrder => RegionPreferences.PriorityRegionIds.Count == 0 ? "未选择恢复地区" :
        "恢复优先级：" + string.Join(" → ", RegionPreferences.PriorityRegionIds.Select(id => RegionResolver.ForId(id)!.Name));
    public string ToggleText => Running ? "■  停止服务" : "▶  启动服务";
    public string Summary => $"{(Running ? "监控中" : "已停止")}     ·     {Rows.Count} 个 agent     ·     {Rows.Count(r => r.Row.Deadline is not null)} 个冷却中";
    public string Connection => uiError ?? snapshot.Connection;
    public string UpdatedAt => snapshot.LastScan is { } date ? "更新于 " + date.ToLocalTime().ToString("HH:mm:ss") : "";
    public bool HasNoRows => Rows.Count == 0;
    public string EmptyTitle => Running ? "正在等待 agent" : "准备就绪";
    public string EmptyDescription => Running ? "运行中的本机 Herdr agent 会自动出现在这里。" : "点击“启动服务”以发现并监控本机所有 Herdr 会话。";
    public string ProxyTitle => !ProxyRecoveryEnabled ? "Clash 自动恢复已关闭" : snapshot.Proxy.Phase switch
    {
        ProxyPhase.Stopped => "代理监控已停止", ProxyPhase.Checking => "检查代理", ProxyPhase.Healthy => "代理正常",
        ProxyPhase.Suspect => "代理异常，等待复核", ProxyPhase.Testing => "地区节点测速中", ProxyPhase.Switching => "切换代理节点",
        ProxyPhase.CoolingDown => "节点切换冷却中", ProxyPhase.Retrying => "等待重新测速", _ => "自动切换暂不可用"
    };
    public string ProxyDetail => !ProxyRecoveryEnabled ? "不自动探测、测速或切换节点；agent 可独立续接。" : snapshot.Proxy.Detail;
    public string SelectedNode => "当前节点：" + (snapshot.Proxy.Sources.SelectMany(s=>s.Nodes).FirstOrDefault(n=>n.Name==snapshot.Proxy.Selected) is { } node ? node.SourceName + " · " + node.OriginalName : snapshot.Proxy.Selected ?? "等待读取") +
        (snapshot.Proxy.CurrentRegionId is { } id ? " · " + RegionResolver.ForId(id)!.Name : "");
    public string ProxyNumbers => snapshot.Proxy.Phase == ProxyPhase.Testing
        ? $"{RegionResolver.ForId(snapshot.Proxy.TestingRegionId ?? "")?.Name} · {snapshot.Proxy.Completed} / {snapshot.Proxy.CandidateCount}"
        : $"{SourceSummary} · 已选 {RegionPreferences.PriorityRegionIds.Count} 个地区" + (snapshot.Proxy.LatencyMs is { } latency ? $"  ·  连通检查 {latency} ms" : "");
    public string ProxyCountdown => !ProxyRecoveryEnabled ? "" : snapshot.Proxy.Phase == ProxyPhase.CoolingDown && snapshot.Proxy.SwitchDeadline is { } deadline
        ? $"{Math.Max(0, (int)Math.Ceiling(deadline - new MonotonicClock().Now))} 秒后复核切换"
        : snapshot.Proxy.Phase is ProxyPhase.Retrying or ProxyPhase.Suspect or ProxyPhase.Unavailable && snapshot.Proxy.NextCheck is { } next
        ? $"{Math.Max(0, (int)Math.Ceiling(next - new MonotonicClock().Now))} 秒后重试" : "";
    public bool HasDelays => Delays.Count > 0;
    public string DelayTitle => $"第 {snapshot.Proxy.Round} 轮 · 地区测速结果";

    public MainViewModel(MonitorEngine engine, string logPath, IRegionPreferencesStore? preferencesStore = null)
    {
        this.engine = engine; LogPath = logPath; snapshot = engine.Snapshot;
        ToggleCommand = new ToggleServiceCommand(this);
        regionSettings = new(preferencesStore ?? new RegionPreferencesStore(), engine.Proxy);
        engine.SnapshotChanged += OnSnapshot;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        { foreach (var row in Rows) row.Tick(); Changed(nameof(ProxyCountdown)); });
        timer.Start();
    }
    public Task InitializeAsync() => initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        try
        {
            uiError = await regionSettings.InitializeAsync(lifetime.Token, ProxyRecoveryEnabled);
            initialized = true; Changed("");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) { uiError = "地区设置初始化失败：" + e.Message; initialized = true; Changed(""); }
    }
    public Task RefreshRegionsAsync(CancellationToken cancellationToken = default) => RefreshCoreAsync(cancellationToken);
    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await engine.Proxy.RefreshCatalogueAsync(linked.Token);
    }
    public async Task ApplyRegionsAsync(ProxyRegionPreferences preferences, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await regionSettings.ApplyAsync(preferences, linked.Token);
        uiError = null;
        Changed("");
    }
    public async Task RestoreSourcesAsync(CancellationToken ct)
    {
        await regionSettings.RestoreAsync(ct);
        uiError = null; Changed("");
    }
    private void OnSnapshot(MonitorSnapshot value) => Dispatcher.UIThread.Post(() => Apply(value));
    private void Apply(MonitorSnapshot value)
    {
        snapshot = value;
        var existing = Rows.ToDictionary(r => r.Row.Id);
        for (var i = Rows.Count - 1; i >= 0; i--) if (!value.Rows.Any(r => r.Id == Rows[i].Row.Id)) Rows.RemoveAt(i);
        for (var i = 0; i < value.Rows.Count; i++)
        {
            var row = value.Rows[i];
            if (!existing.TryGetValue(row.Id, out var model)) { model = new(row); Rows.Insert(i, model); }
            else { model.Update(row); var current = Rows.IndexOf(model); if (current != i) Rows.Move(current, i); }
        }
        Events.Clear(); foreach (var valueEvent in value.Events.Reverse()) Events.Add(valueEvent);
        Delays.Clear(); foreach (var delay in value.Proxy.Delays) Delays.Add($"{RegionResolver.ForId(delay.RegionId ?? "")?.Name ?? "未知地区"} · {delay.SourceName} · {delay.OriginalName ?? delay.Name}     {delay.Milliseconds} ms");
        Changed("");
    }
    public async Task ToggleAsync()
    {
        if (!CanToggle) return;
        busy = true; uiError = null; Changed(nameof(CanToggle));
        try { if (Running) await engine.StopAsync(); else await engine.StartAsync(); }
        catch (Exception e) { uiError = "操作失败：" + e.Message; Changed(nameof(Connection)); }
        finally { busy = false; Changed(nameof(CanToggle)); }
    }
    public void RevealLog()
    {
        try { AppPaths.RevealLog(LogPath); }
        catch (Exception e) { uiError = "无法打开日志：" + e.Message; Changed(nameof(Connection)); }
    }
    public Task StopAsync() => engine.StopAsync();
    public async Task StartServiceAsync()
    {
        await InitializeAsync();
        if (lifetime.IsCancellationRequested) return;
        busy = true; Changed(nameof(CanToggle));
        try { await engine.StartAsync(cancellationToken: lifetime.Token); }
        finally { busy = false; Changed(nameof(CanToggle)); }
    }
    public void SetError(string message) { uiError = message; Changed(nameof(Connection)); }
    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel(); timer.Stop(); engine.SnapshotChanged -= OnSnapshot;
        await engine.DisposeAsync();
        if (initialization is not null) await initialization;
        lifetime.Dispose();
    }
    private sealed class ToggleServiceCommand(MainViewModel owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => owner.CanToggle;
        public async void Execute(object? parameter) => await owner.ToggleAsync();
    }
}
