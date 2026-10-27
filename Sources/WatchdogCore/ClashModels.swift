import Foundation

public struct ClashNode: Codable, Equatable, Sendable {
    public let name: String
    public let type: String
    public var now: String?
    public var all: [String]?
    public init(name: String, type: String = "Vless", now: String? = nil, all: [String]? = nil) {
        self.name = name; self.type = type; self.now = now; self.all = all
    }
    public var isJapaneseLeaf: Bool {
        let excluded = ["direct", "reject", "rejectdrop", "pass", "compatible", "selector", "urltest", "fallback", "loadbalance", "relay"]
        guard all == nil, !excluded.contains(type.lowercased()) else { return false }
        // Region comes from subscription labels; never infer it from an unbounded substring like "jp".
        return name.range(of: "🇯🇵|日本|(?i:\\bjapan\\b|(?<![a-z0-9])jp(?![a-z0-9]))", options: .regularExpression) != nil
    }
}

public struct ClashSnapshot: Sendable, Equatable {
    public let endpoint: String
    public let mode: String
    public let mixedPort: Int
    public let nodes: [String: ClashNode]
    public init(endpoint: String, mode: String = "global", mixedPort: Int = 7890, nodes: [String: ClashNode]) {
        self.endpoint = endpoint; self.mode = mode; self.mixedPort = mixedPort; self.nodes = nodes
    }
    public var selected: String? { nodes["GLOBAL"]?.now }
    public var canSelect: Bool { mode.lowercased() == "global" && mixedPort == 7890 && nodes["GLOBAL"]?.type.lowercased() == "selector" }
    public var candidates: [ClashNode] {
        (nodes["GLOBAL"]?.all ?? []).compactMap { nodes[$0] }.filter(\.isJapaneseLeaf).sorted { $0.name < $1.name }
    }
    public func hasSameSelectionContext(as other: ClashSnapshot) -> Bool {
        hasSameConfiguration(as: other) && selected == other.selected
    }
    public func hasSameConfiguration(as other: ClashSnapshot) -> Bool {
        endpoint == other.endpoint && mode == other.mode && mixedPort == other.mixedPort
            && nodes["GLOBAL"]?.type == other.nodes["GLOBAL"]?.type
            && nodes["GLOBAL"]?.all == other.nodes["GLOBAL"]?.all && candidates == other.candidates
    }
}

public struct ProxyProbe: Sendable {
    public let healthy: Bool
    public let latencyMS: Int?
    public init(healthy: Bool, latencyMS: Int? = nil) { self.healthy = healthy; self.latencyMS = latencyMS }
}

public struct NodeDelay: Sendable, Equatable {
    public let name: String
    public let milliseconds: Int
    public init(name: String, milliseconds: Int) { self.name = name; self.milliseconds = milliseconds }
}

public enum ProxyPhase: String, Sendable {
    case stopped = "已停止", checking = "检查代理", healthy = "代理正常", suspect = "代理异常，等待复核"
    case testing = "日本节点测速中", switching = "切换日本节点", retrying = "等待重新测速", unavailable = "自动切换暂不可用"
}

public struct ProxyStatus: Sendable {
    public var phase: ProxyPhase = .stopped
    public var selected: String?
    public var detail = "启动服务后检查 127.0.0.1:7890"
    public var healthyAt: TimeInterval?
    public var latencyMS: Int?
    public var candidateCount = 0
    public var completed = 0
    public var round = 0
    public var nextCheck: TimeInterval?
    public var delays: [NodeDelay] = []
    public var permitsAgent: Bool { phase == .healthy }
    public func permitsAgent(at now: TimeInterval) -> Bool {
        phase == .healthy && healthyAt.map { now >= $0 && now - $0 <= 20 } == true
    }
}

public struct ProxyRecoveryPolicy {
    public private(set) var failures = 0
    public private(set) var round = 0
    public init() {}
    public mutating func observe(healthy: Bool) { failures = healthy ? 0 : failures + 1 }
    public var shouldRecover: Bool { failures >= 2 }
    public mutating func beginRound() { round += 1 }
    public static func ranked(_ delays: [NodeDelay], allowed: Set<String>) -> [NodeDelay] {
        delays.filter { allowed.contains($0.name) && $0.milliseconds > 0 && $0.milliseconds <= 5000 }
            .sorted { $0.milliseconds == $1.milliseconds ? $0.name < $1.name : $0.milliseconds < $1.milliseconds }
    }
}

public protocol ClashService: Sendable {
    func snapshot() async throws -> ClashSnapshot
    func probeProxy() async -> ProxyProbe
    func delay(node: String, endpoint: String, url: String) async throws -> Int
    func select(node: String, snapshot: ClashSnapshot) async throws
}
