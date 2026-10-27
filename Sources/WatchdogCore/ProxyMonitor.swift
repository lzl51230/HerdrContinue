import Foundation

@MainActor
public final class ProxyMonitor {
    public private(set) var status = ProxyStatus()
    public private(set) var running = false
    public private(set) var healthEpoch: UInt64 = 0
    public var onStatus: ((ProxyStatus) -> Void)?
    public var onEvent: ((String) -> Void)?
    private let service: any ClashService
    private let clock: () -> TimeInterval
    private let interval: TimeInterval
    private var task: Task<Void, Never>?
    private var generation = UUID()
    private var policy = ProxyRecoveryPolicy()
    private var checking = false
    private var lastEvent: String?

    public init(service: any ClashService = LiveClashService(), interval: TimeInterval = 10,
                clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.service = service; self.interval = interval; self.clock = clock
    }
    public var permitsAgent: Bool { running && status.permitsAgent(at: clock()) }

    public func start(automatic: Bool = true) {
        guard !running else { return }
        running = true; generation = UUID(); healthEpoch &+= 1; policy = ProxyRecoveryPolicy(); checking = false
        status = ProxyStatus(); phase(.checking, "正在检查 127.0.0.1:7890")
        guard automatic else { return }
        let token = generation
        task = Task { [weak self] in
            while let self, self.active(token) {
                await self.checkNow()
                guard self.active(token) else { return }
                let delay = max(0.01, (self.status.nextCheck ?? self.clock() + self.interval) - self.clock())
                do { try await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000)) }
                catch { return }
            }
        }
    }
    public func stop() {
        guard running else { return }
        running = false; generation = UUID(); healthEpoch &+= 1; task?.cancel(); task = nil; checking = false
        status.nextCheck = nil; phase(.stopped, "代理监控已停止；保留当前节点")
    }
    private func active(_ token: UUID) -> Bool { running && generation == token && !Task.isCancelled }

    /// A single cycle is exposed for deterministic fault-injection tests; the app uses the automatic loop.
    public func checkNow() async {
        guard running, !checking else { return }
        checking = true
        let token = generation, started = clock(), previousRound = policy.round
        defer {
            if generation == token {
                checking = false
                if running {
                    status.nextCheck = policy.round > previousRound ? clock() + interval : max(clock() + 0.01, started + interval)
                    publish()
                }
            }
        }
        async let config = readSnapshot()
        let probe = await service.probeProxy()
        guard active(token) else { return }
        policy.observe(healthy: probe.healthy)
        // Close the gate as soon as both checks fail, without waiting for the control API.
        if probe.healthy { healthy(probe) }
        else { phase(.suspect, "代理请求失败；暂停 agent 续接（连续 \(policy.failures) 轮）") }
        let (snapshot, error) = await config
        guard active(token) else { return }
        if let snapshot { apply(snapshot) }
        if probe.healthy {
            if let error { status.detail = "代理可用；\(error)" }
            else if snapshot?.canSelect == false { status.detail = "代理可用；非全局模式，自动切换暂停" }
            publish(); return
        }
        guard policy.shouldRecover else { return }
        guard let snapshot else { phase(.unavailable, error ?? "等待 Clash 控制接口恢复"); return }
        guard snapshot.canSelect else { phase(.unavailable, "当前不是全局 GLOBAL 选择模式，暂停自动切换"); return }
        await recover(snapshot, token: token)
    }

    private func recover(_ initial: ClashSnapshot, token: UUID) async {
        policy.beginRound(); status.round = policy.round; status.delays = []; status.completed = 0
        let candidates = initial.candidates
        status.candidateCount = candidates.count
        guard !candidates.isEmpty else { phase(.retrying, "未发现可选择的日本节点；10 秒后重新检查"); return }
        var delays: [NodeDelay] = []
        for (index, url) in LiveClashService.testURLs.enumerated() {
            status.completed = 0
            phase(.testing, "第 \(policy.round) 轮 · \(index == 0 ? "主地址" : "备用地址") · 仅测试日本节点")
            delays = await measure(candidates, endpoint: initial.endpoint, url: url, token: token)
            guard active(token) else { return }
            if !delays.isEmpty { break }
        }
        guard active(token) else { return }
        let ranked = ProxyRecoveryPolicy.ranked(delays, allowed: Set(candidates.map(\.name)))
        status.delays = ranked
        guard !ranked.isEmpty else { phase(.retrying, "全部日本节点超时或不可用；10 秒后重新测速"); return }

        // A manual change or a profile reload invalidates every measurement from this round.
        guard let fresh = await checkedSnapshot(matching: initial, token: token) else { return }
        let recovered = await service.probeProxy()
        guard active(token) else { return }
        if recovered.healthy { healthy(recovered); return }
        var expected = fresh
        for candidate in ranked {
            guard active(token), let before = await checkedSnapshot(matching: expected, token: token) else { return }
            phase(.switching, "验证 \(candidate.name) · \(candidate.milliseconds) ms")
            var writeError: Error?
            if before.selected != candidate.name {
                do {
                    try Task.checkCancellation()
                    try await service.select(node: candidate.name, snapshot: before)
                } catch { writeError = error }
            }
            guard active(token) else { return }
            let (after, error) = await readSnapshot()
            guard active(token) else { return }
            guard let after else { phase(.unavailable, error ?? "无法确认节点切换结果；等待重新连接"); return }
            apply(after)
            guard after.hasSameConfiguration(as: before), after.selected == candidate.name else {
                phase(.retrying, writeError == nil ? "节点选择或配置已变化；放弃本轮结果，稍后复核" : "切换未确认；稍后读取实际选择再重试")
                return
            }
            // Even if PUT timed out, a matching readback proves which node is selected.
            event("已选择日本节点：\(candidate.name)（\(candidate.milliseconds) ms）；验证代理连通性")
            let verification = await service.probeProxy()
            guard active(token) else { return }
            guard let confirmed = await checkedSnapshot(matching: after, token: token) else { return }
            if verification.healthy {
                apply(confirmed); healthy(verification)
                event("代理已恢复：\(candidate.name)；符合条件的 agent 将重新冷却 10 秒")
                return
            }
            event("\(candidate.name) 实际代理请求失败，尝试下一可用日本节点")
            expected = confirmed
        }
        phase(.retrying, "本轮日本节点实际连通性均未通过；10 秒后重新测速")
    }

    private func measure(_ nodes: [ClashNode], endpoint: String, url: String, token: UUID) async -> [NodeDelay] {
        var results: [NodeDelay] = []
        // Bound concurrency across both transport tasks and server-side delay checks.
        for offset in stride(from: 0, to: nodes.count, by: 4) {
            guard active(token) else { break }
            await withTaskGroup(of: NodeDelay?.self) { group in
                for node in nodes[offset..<min(offset + 4, nodes.count)] {
                    group.addTask { [service] in
                        guard !Task.isCancelled else { return nil }
                        guard let delay = try? await service.delay(node: node.name, endpoint: endpoint, url: url), delay > 0, delay <= 5000 else { return nil }
                        return NodeDelay(name: node.name, milliseconds: delay)
                    }
                }
                for await result in group {
                    guard active(token) else { group.cancelAll(); continue }
                    status.completed += 1
                    if let result { results.append(result) }
                    status.delays = ProxyRecoveryPolicy.ranked(results, allowed: Set(nodes.map(\.name)))
                    publish()
                }
            }
        }
        return results
    }
    private func checkedSnapshot(matching expected: ClashSnapshot, token: UUID) async -> ClashSnapshot? {
        let (fresh, error) = await readSnapshot()
        guard active(token) else { return nil }
        guard let fresh else { phase(.unavailable, error ?? "Clash 连接失败"); return nil }
        apply(fresh)
        guard fresh.canSelect, fresh.hasSameSelectionContext(as: expected) else {
            phase(.retrying, "节点选择或配置已变化；放弃旧测速结果，稍后复核")
            return nil
        }
        return fresh
    }
    private func readSnapshot() async -> (ClashSnapshot?, String?) {
        do { return (try await service.snapshot(), nil) }
        catch { return (nil, error.localizedDescription) }
    }
    private func apply(_ snapshot: ClashSnapshot) {
        status.selected = snapshot.selected; status.candidateCount = snapshot.candidates.count
        publish()
    }
    private func healthy(_ probe: ProxyProbe) {
        policy.observe(healthy: true)
        status.healthyAt = clock(); status.latencyMS = probe.latencyMS
        phase(.healthy, "代理连通；保持当前节点，故障后才切换")
    }
    private func phase(_ phase: ProxyPhase, _ detail: String) {
        if status.phase == .healthy && phase != .healthy { healthEpoch &+= 1 }
        status.phase = phase; status.detail = detail
        if phase != .healthy { status.healthyAt = nil }
        publish(); event(detail)
    }
    private func publish() { onStatus?(status) }
    private func event(_ message: String) {
        guard lastEvent != message else { return }
        lastEvent = message; onEvent?(message)
    }
}
