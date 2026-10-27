import XCTest
@testable import WatchdogCore

private actor FakeService: HerdrService {
    var value = observation()
    var sent = 0
    var reads = 0
    var failDiscovery = false
    var replacementOnRefresh = false
    var slowRefresh = false
    func sessions() async throws -> [Session] {
        if failDiscovery { throw HerdrError("offline") }
        return [testSession]
    }
    func agents(in session: Session) async throws -> [Agent] { [value.agent] }
    func observe(_ agent: Agent, in session: Session, refresh: Bool) async throws -> Observation {
        reads += 1
        if refresh && slowRefresh { try await Task.sleep(nanoseconds: 200_000_000) }
        if refresh && replacementOnRefresh { return observation(pid: 999) }
        return value
    }
    func resume(_ observation: Observation) async throws { try Task.checkCancellation(); sent += 1 }
    func setReplacement() { replacementOnRefresh = true }
    func goOffline() { failDiscovery = true }
    func setSlowRefresh() { slowRefresh = true }
}

@MainActor
final class MonitorTests: XCTestCase {
    func testStopCancelsPendingSendAndRepeatedStartDoesNotDuplicate() async throws {
        let service = FakeService()
        var now = 0.0
        let monitor = Monitor(service: service, proxyService: FakeClash(), pollInterval: 0.01, proxyInterval: 0.01, clock: { now }, logURL: temporaryLog())
        monitor.start(); monitor.start()
        try await Task.sleep(nanoseconds: 80_000_000)
        XCTAssertEqual(monitor.rows.count, 1)
        XCTAssertNotNil(monitor.rows.first?.deadline)
        monitor.stop(); now = 50
        try await Task.sleep(nanoseconds: 50_000_000)
        let sent = await service.sent
        XCTAssertEqual(sent, 0)
        XCTAssertFalse(monitor.running)
    }
    func testMonitorSubmitsOnceThenWaitsForNewEvidence() async throws {
        let service = FakeService()
        var now = 0.0
        let monitor = Monitor(service: service, proxyService: FakeClash(), pollInterval: 0.01, proxyInterval: 0.01, clock: { now }, logURL: temporaryLog())
        monitor.start()
        try await Task.sleep(nanoseconds: 70_000_000)
        now = 10
        try await Task.sleep(nanoseconds: 100_000_000)
        now = 100
        try await Task.sleep(nanoseconds: 50_000_000)
        monitor.stop()
        let sent = await service.sent
        XCTAssertEqual(sent, 1)
        XCTAssertTrue(monitor.events.contains { $0.message.contains("Herdr 已确认提交") })
    }
    func testFreshIdentityCheckPreventsSendingToReplacement() async throws {
        let service = FakeService()
        var now = 0.0
        let monitor = Monitor(service: service, proxyService: FakeClash(), pollInterval: 0.01, proxyInterval: 0.01, clock: { now }, logURL: temporaryLog())
        monitor.start()
        try await Task.sleep(nanoseconds: 70_000_000)
        await service.setReplacement(); now = 10
        try await Task.sleep(nanoseconds: 70_000_000)
        monitor.stop()
        let sent = await service.sent
        XCTAssertEqual(sent, 0)
    }
    func testDiscoveryFailureClearsPendingCountdown() async throws {
        let service = FakeService()
        var now = 0.0
        let monitor = Monitor(service: service, proxyService: FakeClash(), pollInterval: 0.01, proxyInterval: 0.01, clock: { now }, logURL: temporaryLog())
        monitor.start()
        try await Task.sleep(nanoseconds: 70_000_000)
        await service.goOffline(); now = 20
        try await Task.sleep(nanoseconds: 70_000_000)
        XCTAssertNil(monitor.rows.first?.deadline)
        monitor.stop()
        let sent = await service.sent
        XCTAssertEqual(sent, 0)
    }
    func testProxyOutageBlocksAndRecoveryStartsFullCooldown() async throws {
        let service = FakeService(), clash = FakeClash()
        await clash.setHealthy(false); await clash.setDelays([:])
        var now = 0.0
        let monitor = Monitor(service: service, proxyService: clash, pollInterval: 0.01, proxyInterval: 0.01,
                              clock: { now }, logURL: temporaryLog())
        monitor.start()
        try await Task.sleep(nanoseconds: 70_000_000)
        now = 100
        try await Task.sleep(nanoseconds: 50_000_000)
        let duringOutage = await service.sent
        XCTAssertEqual(duringOutage, 0); XCTAssertNil(monitor.rows.first?.deadline)
        await clash.setHealthy(true)
        try await Task.sleep(nanoseconds: 80_000_000)
        XCTAssertEqual(monitor.rows.first?.deadline, 110)
        now = 109.99
        try await Task.sleep(nanoseconds: 30_000_000)
        let beforeCooldown = await service.sent; XCTAssertEqual(beforeCooldown, 0)
        now = 110
        try await Task.sleep(nanoseconds: 80_000_000)
        let afterRecovery = await service.sent; XCTAssertEqual(afterRecovery, 1)
        monitor.stop(); XCTAssertEqual(monitor.proxyStatus.phase, .stopped)
    }
    func testProxyFailureDuringAgentRevalidationCancelsPendingInput() async throws {
        let service = FakeService(), clash = FakeClash()
        await service.setSlowRefresh(); await clash.setDelays([:])
        var now = 0.0
        let monitor = Monitor(service: service, proxyService: clash, pollInterval: 0.01, proxyInterval: 0.01,
                              clock: { now }, logURL: temporaryLog())
        monitor.start()
        try await Task.sleep(nanoseconds: 70_000_000)
        now = 10
        try await Task.sleep(nanoseconds: 30_000_000)
        await clash.setHealthy(false)
        try await Task.sleep(nanoseconds: 250_000_000)
        let sent = await service.sent
        XCTAssertEqual(sent, 0); XCTAssertNil(monitor.rows.first?.deadline)
        monitor.stop()
    }
    private func temporaryLog() -> URL { FileManager.default.temporaryDirectory.appendingPathComponent("watchdog-tests-\(UUID()).jsonl") }
}
