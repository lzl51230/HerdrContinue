import Foundation

public struct Session: Codable, Hashable, Sendable {
    public let name: String
    public let running: Bool
    public let socket_path: String
    public init(name: String, running: Bool = true, socket_path: String) {
        self.name = name; self.running = running; self.socket_path = socket_path
    }
}

public struct Agent: Codable, Sendable {
    public var agent: String?
    public var agent_status: String
    public var pane_id: String
    public var terminal_id: String
    public var workspace_id: String
    public var tab_id: String
    public var state_change_seq: UInt64?
    public var revision: UInt64?
    public var name: String?
    public var terminal_title_stripped: String?
    public var foreground_cwd: String?
    public var agent_session: AgentSession?
    public var interactive_ready: Bool?
    public var launch_pending: Bool?

    public init(kind: String = "codex", status: String = "idle", pane: String = "w1:p1",
                terminal: String = "term_test", sequence: UInt64 = 1) {
        agent = kind; agent_status = status; pane_id = pane; terminal_id = terminal
        workspace_id = "w1"; tab_id = "w1:t1"; state_change_seq = sequence
    }
    public var acceptsInput: Bool {
        ["idle", "done"].contains(agent_status) && interactive_ready != false && launch_pending != true
    }
}

public struct AgentSession: Codable, Sendable {
    public var kind: String
    public var value: String
}

public struct Observation: Sendable {
    public let session: Session
    public let agent: Agent
    public let text: String
    public let foregroundPID: Int?
    public let transcript: String?
    public let error: String?

    public init(session: Session, agent: Agent, text: String = "", foregroundPID: Int? = 100,
                transcript: String? = nil, error: String? = nil) {
        self.session = session; self.agent = agent; self.text = text
        self.foregroundPID = foregroundPID; self.transcript = transcript; self.error = error
    }
    // terminal_id survives pane moves. PID/session distinguish a replacement in the same terminal.
    public var key: String { session.socket_path + "|" + agent.terminal_id }
    public var occupant: String {
        "\(agent.agent ?? "")|\(foregroundPID ?? -1)|\(agent.agent_session?.value ?? "")"
    }
}

public enum FailureReason: String, Sendable {
    case capacity = "模型容量不足 / 服务过载"
    case network = "网络连接 / 传输失败"
}

public struct FailureEvidence: Equatable, Sendable {
    public let reason: FailureReason
    public let message: String
    // A stable transcript boundary, not a terminal revision or a clock in the footer.
    public let fingerprint: String
}

public enum Assessment: Equatable, Sendable {
    case failure(FailureEvidence)
    case skip(String)
}

public struct AgentRow: Identifiable, Sendable {
    public var id: String
    public var title: String
    public var location: String
    public var kind: String
    public var state: String
    public var detail: String
    public var deadline: TimeInterval?
    public var attempts: Int
}

public struct MonitorEvent: Identifiable, Sendable {
    public let id = UUID()
    public let date = Date()
    public let target: String
    public let message: String
    public init(target: String, message: String) { self.target = target; self.message = message }
}
