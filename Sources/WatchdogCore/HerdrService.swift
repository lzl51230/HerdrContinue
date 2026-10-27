import Foundation

public protocol HerdrService: Sendable {
    func sessions() async throws -> [Session]
    func agents(in session: Session) async throws -> [Agent]
    func observe(_ agent: Agent, in session: Session, refresh: Bool) async throws -> Observation
    func resume(_ observation: Observation) async throws
}

public struct LiveHerdrService: HerdrService, Sendable {
    public let discovery: SessionDiscovery
    public let rpc: SocketRPC
    public init(binary: String? = nil, rpc: SocketRPC = SocketRPC()) {
        discovery = SessionDiscovery(binary: binary); self.rpc = rpc
    }
    public func sessions() async throws -> [Session] { try await discovery.sessions() }
    public func agents(in session: Session) async throws -> [Agent] {
        struct Result: Decodable { var agents: [Agent] }
        return try JSONDecoder().decode(Result.self, from: await rpc.call(socketPath: session.socket_path, method: "agent.list")).agents
    }
    public func observe(_ original: Agent, in session: Session, refresh: Bool = false) async throws -> Observation {
        var agent = original
        if refresh {
            struct Result: Decodable { var agent: Agent }
            agent = try JSONDecoder().decode(Result.self, from: await rpc.call(socketPath: session.socket_path, method: "agent.get", params: ["target": original.pane_id])).agent
        }
        guard agent.acceptsInput else { return Observation(session: session, agent: agent, foregroundPID: nil) }
        struct ReadResult: Decodable {
            struct Read: Decodable { var text: String }
            var read: Read
        }
        struct ProcessResult: Decodable {
            struct Info: Decodable { var foreground_process_group_id: Int? }
            var process_info: Info
        }
        let pane = agent.pane_id
        async let readData = rpc.call(socketPath: session.socket_path, method: "agent.read", params: ["target": pane, "source": "detection", "format": "text"])
        async let processData = rpc.call(socketPath: session.socket_path, method: "pane.process_info", params: ["pane_id": pane])
        let text = try JSONDecoder().decode(ReadResult.self, from: await readData).read.text
        let pid = try JSONDecoder().decode(ProcessResult.self, from: await processData).process_info.foreground_process_group_id
        var transcript: String?
        if agent.agent == "omp", let reference = agent.agent_session, reference.kind == "path" {
            transcript = readTranscript(reference.value)
        }
        return Observation(session: session, agent: agent, text: text, foregroundPID: pid, transcript: transcript)
    }
    public func resume(_ observation: Observation) async throws {
        try Task.checkCancellation()
        // Server-owned wait observes short working transitions that a polling client could miss.
        _ = try await SocketRPC(timeout: 8).call(socketPath: observation.session.socket_path, method: "agent.prompt",
                                               params: ["target": observation.agent.pane_id, "text": "继续"], waitForWorking: true)
    }
    private func readTranscript(_ path: String) -> String? {
        let url = URL(fileURLWithPath: path).resolvingSymlinksInPath()
        let root = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".omp/agent/sessions").resolvingSymlinksInPath().path + "/"
        guard url.path.hasPrefix(root), url.pathExtension == "jsonl", let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let size = try? handle.seekToEnd() else { return nil }
        let start = size > 262144 ? size - 262144 : 0
        try? handle.seek(toOffset: start)
        guard let data = try? handle.readToEnd() else { return nil }
        var bytes = data
        // Drop a partial first record when the bounded tail starts mid-record.
        if start > 0, let newline = bytes.firstIndex(of: 10) { bytes = bytes.suffix(from: bytes.index(after: newline)) }
        return String(data: bytes, encoding: .utf8)
    }
}
