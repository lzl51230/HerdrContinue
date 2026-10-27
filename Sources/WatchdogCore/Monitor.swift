import Foundation
import Combine

@MainActor
public final class Monitor: ObservableObject {
    @Published public private(set) var running = false
    @Published public private(set) var rows: [AgentRow] = []
    @Published public private(set) var events: [MonitorEvent] = []
    @Published public private(set) var connection = "服务已停止"
    @Published public private(set) var lastScan: Date?
    @Published public private(set) var proxyStatus = ProxyStatus()
    public let logURL: URL
    private let service: any HerdrService
    private let clock: () -> TimeInterval
    private let pollInterval: TimeInterval
    private var machine = RecoveryMachine()
    private var task: Task<Void, Never>?
    private var generation = UUID()
    private var rowDetails: [String: String] = [:]
    private var lastConnectionError: String?
    private let proxy: ProxyMonitor
    private struct PendingAttempt { let id: UUID; let task: Task<Void, Never>; var submitted = false }
    private var attempts: [String: PendingAttempt] = [:]

    public init(service: any HerdrService = LiveHerdrService(), proxyService: any ClashService = LiveClashService(),
                pollInterval: TimeInterval = 2, proxyInterval: TimeInterval = 10,
                clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }, logURL: URL? = nil) {
        self.service = service; self.pollInterval = pollInterval; self.clock = clock
        self.proxy = ProxyMonitor(service: proxyService, interval: proxyInterval, clock: clock)
        self.logURL = logURL ?? FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/HerdrAutoContinue/events.jsonl")
        proxy.onStatus = { [weak self] status in
            guard let self else { return }
            self.proxyStatus = status
            if !status.permitsAgent(at: self.clock()) {
                self.machine.stop()
                for attempt in self.attempts.values where !attempt.submitted { attempt.task.cancel() }
                for index in self.rows.indices where self.rows[index].deadline != nil {
                    self.rows[index].deadline = nil; self.rows[index].detail = "等待代理恢复"
                }
            }
        }
        proxy.onEvent = { [weak self] message in self?.append("Clash", message) }
    }

    public func start() {
        guard !running else { return }
        running = true; generation = UUID(); connection = "正在连接 Herdr…"
        lastConnectionError = nil
        append("服务", "开始监控；每个 agent 独立冷却 10 秒")
        proxy.start()
        let token = generation
        task = Task { [weak self] in
            while !Task.isCancelled {
                guard let self, self.running, self.generation == token else { return }
                await self.scan(token: token)
                do { try await Task.sleep(nanoseconds: UInt64(self.pollInterval * 1_000_000_000)) }
                catch { return }
            }
        }
    }

    public func stop() {
        guard running else { return }
        running = false; generation = UUID(); task?.cancel(); task = nil
        proxy.stop()
        for attempt in attempts.values { attempt.task.cancel() }
        attempts.removeAll()
        machine.stop(); connection = "服务已停止"
        for index in rows.indices { rows[index].deadline = nil; rows[index].detail = "监控已停止" }
        rowDetails.removeAll()
        append("服务", "已停止；取消全部待发送任务")
    }

    private func active(_ token: UUID) -> Bool { running && generation == token && !Task.isCancelled }

    private func scan(token: UUID) async {
        do {
            let sessions = try await service.sessions()
            guard active(token) else { return }
            var observed: [Observation] = []
            var failedSockets: Set<String> = []
            // Sessions and agents are isolated tasks: a stalled socket cannot hold up another agent's cooldown.
            await withTaskGroup(of: (String, [Observation]?, String?).self) { group in
                for session in sessions {
                    group.addTask { [service] in
                        do {
                            let agents = try await service.agents(in: session)
                            var observations: [Observation] = []
                            // Cap in-flight reads to eight per session.
                            for offset in stride(from: 0, to: agents.count, by: 8) {
                                let chunk = agents[offset..<min(offset + 8, agents.count)]
                                await withTaskGroup(of: Observation.self) { reads in
                                    for agent in chunk {
                                        reads.addTask {
                                            do { return try await service.observe(agent, in: session, refresh: false) }
                                            catch { return Observation(session: session, agent: agent, foregroundPID: nil, error: error.localizedDescription) }
                                        }
                                    }
                                    for await observation in reads { observations.append(observation) }
                                }
                            }
                            return (session.socket_path, observations, nil)
                        } catch { return (session.socket_path, nil, error.localizedDescription) }
                    }
                }
                for await (socket, observations, error) in group {
                    guard active(token) else { group.cancelAll(); continue }
                    if let observations { observed += observations }
                    else {
                        failedSockets.insert(socket)
                        let message = error ?? "连接失败"
                        if lastConnectionError != message { append("连接", message); lastConnectionError = message }
                    }
                }
            }
            guard active(token) else { return }
            connection = sessions.isEmpty ? "等待 Herdr 启动…" : "已连接 \(sessions.count - failedSockets.count) 个会话 · \(observed.count) 个 agent"
            if !failedSockets.isEmpty { connection += " · 部分连接异常，正在重试" }
            else { lastConnectionError = nil }
            lastScan = Date()
            var nextRows: [AgentRow] = []
            var intents: [ResumeIntent] = []
            let proxyReady = proxy.permitsAgent
            if !proxyReady { machine.stop() }
            for observation in observed {
                var (row, intent) = machine.evaluate(observation, now: clock())
                if !proxyReady {
                    machine.suspend(keys: [observation.key]); intent = nil
                    if row.deadline != nil { row.detail = "等待代理恢复"; row.deadline = nil }
                }
                nextRows.append(row)
                if rowDetails[row.id] != row.detail {
                    append(row.location, row.detail)
                    rowDetails[row.id] = row.detail
                }
                if let intent { intents.append(intent) }
            }
            let disconnectedRows = rows.filter { row in failedSockets.contains(where: { row.id.hasPrefix($0 + "|") }) }
            machine.suspend(keys: Set(rows.map(\.id)).subtracting(Set(observed.map(\.key))))
            nextRows += disconnectedRows.map { row in
                var row = row; row.detail = "会话连接异常，暂停发送"; row.deadline = nil; return row
            }
            // Preserve consumed identities across missing sessions/reconnects. Only UI inventory is removed.
            rows = nextRows.sorted { $0.location.localizedStandardCompare($1.location) == .orderedAscending }
            for intent in intents where attempts[intent.observation.key] == nil {
                let id = UUID(), epoch = proxy.healthEpoch, key = intent.observation.key
                let pending = Task { [weak self] in
                    guard let self else { return }
                    await self.attempt(intent, token: token, proxyEpoch: epoch)
                    if self.attempts[key]?.id == id { self.attempts.removeValue(forKey: key) }
                }
                attempts[key] = PendingAttempt(id: id, task: pending)
            }
        } catch {
            guard active(token) else { return }
            connection = error.localizedDescription
            machine.stop()
            for index in rows.indices { rows[index].deadline = nil; rows[index].detail = "连接异常，暂停发送" }
            if lastConnectionError != connection { append("连接", connection); lastConnectionError = connection }
        }
    }

    private func attempt(_ intent: ResumeIntent, token: UUID, proxyEpoch: UInt64) async {
        let original = intent.observation
        let target = "\(original.session.name) / \(original.agent.pane_id)"
        do {
            let fresh = try await service.observe(original.agent, in: original.session, refresh: true)
            guard active(token), proxy.permitsAgent, proxy.healthEpoch == proxyEpoch else { return }
            guard machine.claim(intent, fresh: fresh, now: clock()) else {
                let (row, _) = machine.evaluate(fresh, now: clock())
                update(row); append(target, "已取消本次发送：状态、输入或 agent 身份发生变化")
                return
            }
            append(target, "发送「继续」并回车；依据：\(intent.evidence.message.prefix(300))")
            attempts[original.key]?.submitted = true
            do {
                try await service.resume(fresh)
                guard active(token) else { return }
                machine.submissionConfirmed(key: fresh.key)
                append(target, "Herdr 已确认提交并观察到 agent 开始运行")
            } catch {
                guard active(token) else { return }
                machine.submissionFailed(key: fresh.key, definitelyNotSent: (error as? HerdrError)?.definitelyNotSent == true, now: clock())
                append(target, "提交异常：\(error.localizedDescription)")
            }
            let (row, _) = machine.evaluate(fresh, now: clock())
            update(row)
        } catch {
            guard active(token) else { return }
            machine.suspend(keys: [original.key])
            append(target, "发送前复核失败，已取消：\(error.localizedDescription)")
        }
    }

    private func update(_ row: AgentRow) {
        if let index = rows.firstIndex(where: { $0.id == row.id }) { rows[index] = row }
    }
    private func append(_ target: String, _ message: String) {
        let event = MonitorEvent(target: target, message: message)
        events.append(event)
        if events.count > 500 { events.removeFirst(events.count - 500) }
        let directory = logURL.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        if let attributes = try? FileManager.default.attributesOfItem(atPath: logURL.path),
           let size = attributes[.size] as? UInt64, size > 2_000_000 {
            let old = logURL.appendingPathExtension("previous")
            try? FileManager.default.removeItem(at: old)
            try? FileManager.default.moveItem(at: logURL, to: old)
        }
        if !FileManager.default.fileExists(atPath: logURL.path) {
            FileManager.default.createFile(atPath: logURL.path, contents: nil, attributes: [.posixPermissions: 0o600])
        }
        guard let data = try? JSONSerialization.data(withJSONObject: ["time": ISO8601DateFormatter().string(from: event.date), "target": target, "message": message]),
              let handle = try? FileHandle(forWritingTo: logURL) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd(); try? handle.write(contentsOf: data + Data([10]))
    }
}
