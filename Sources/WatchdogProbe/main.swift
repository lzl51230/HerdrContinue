import Foundation
import WatchdogCore

@main
struct Probe {
    @MainActor static func main() async {
        do { try await run() }
        catch { fputs("Probe failed: \(error.localizedDescription)\n", stderr); exit(1) }
    }
    @MainActor static func run() async throws {
        let service = LiveHerdrService()
        let args = CommandLine.arguments
        if args.contains("--clash-readonly") {
            let clash = LiveClashService()
            let snapshot = try await clash.snapshot()
            let probe = await clash.probeProxy()
            print("Clash: mode=\(snapshot.mode), mixed-port=\(snapshot.mixedPort), selected=\(snapshot.selected ?? "unknown"), Japanese leaves=\(snapshot.candidates.count)")
            print("Proxy reachable=\(probe.healthy), latency=\(probe.latencyMS ?? -1) ms; no node testing or switching performed")
        } else if args.contains("--monitor-test"), let index = args.firstIndex(of: "--socket"), index + 1 < args.count {
            let path = args[index + 1]
            guard path.contains("watchdog-qa-") else { throw HerdrError("Test monitor only accepts an isolated watchdog-qa- socket") }
            let wrapped = IsolatedService(session: Session(name: "isolated-test", socket_path: path), base: service)
            let monitor = Monitor(service: wrapped, pollInterval: 0.25, logURL: URL(fileURLWithPath: "/tmp/herdr-watchdog-qa-events.jsonl"))
            monitor.start()
            try await Task.sleep(nanoseconds: 15_000_000_000)
            monitor.stop()
            for event in monitor.events { print("\(event.target): \(event.message)") }
            guard monitor.events.contains(where: { $0.message.contains("Herdr 已确认提交") }) else { throw HerdrError("No confirmed test submission") }
        } else {
            let sessions = try await service.sessions()
            print("Read-only discovery: \(sessions.count) running session(s)")
            for session in sessions {
                for agent in try await service.agents(in: session) {
                    let observation = try await service.observe(agent, in: session, refresh: false)
                    print("\(session.name) \(agent.pane_id) \(agent.agent ?? "?") \(agent.agent_status): \(FailureClassifier().assess(observation))")
                }
            }
        }
    }
}

private struct IsolatedService: HerdrService {
    var session: Session
    var base: LiveHerdrService
    func sessions() async throws -> [Session] { [session] }
    func agents(in session: Session) async throws -> [Agent] { try await base.agents(in: session) }
    func observe(_ agent: Agent, in session: Session, refresh: Bool) async throws -> Observation { try await base.observe(agent, in: session, refresh: refresh) }
    func resume(_ observation: Observation) async throws { try await base.resume(observation) }
}
