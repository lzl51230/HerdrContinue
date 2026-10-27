import XCTest
@testable import WatchdogCore

let japanFast = "🇯🇵日本高速02|BGP|CUCM"
let japanSlow = "🇯🇵日本专线01|BGP|流媒体"
let otherNode = "🇺🇸美国01"

func clashSnapshot(selected: String = otherNode, mode: String = "global", endpoint: String = "/tmp/mock.sock",
                   names: [String] = [japanFast, japanSlow, otherNode]) -> ClashSnapshot {
    var nodes = Dictionary(uniqueKeysWithValues: names.map { ($0, ClashNode(name: $0)) })
    nodes["DIRECT"] = ClashNode(name: "DIRECT", type: "Direct")
    nodes["自动选择"] = ClashNode(name: "自动选择", type: "URLTest", now: otherNode, all: names)
    nodes["GLOBAL"] = ClashNode(name: "GLOBAL", type: "Selector", now: selected, all: names + ["DIRECT", "自动选择"])
    return ClashSnapshot(endpoint: endpoint, mode: mode, nodes: nodes)
}

actor FakeClash: ClashService {
    var state = clashSnapshot()
    var healthy = true
    var healthyNodes: Set<String> = []
    var delays = [japanFast: 50, japanSlow: 120, otherNode: 1]
    var fallbackDelays: [String: Int]?
    var selected: [String] = []
    var tested: [String] = []
    var testedURLs: [String] = []
    var failSnapshot = false
    var selectionErrorAfterApplying = false
    var changeDuringTesting: ClashSnapshot?
    var slowTests = false
    var activeTests = 0
    var maxConcurrentTests = 0
    func snapshot() async throws -> ClashSnapshot {
        if failSnapshot { throw HerdrError("mock offline") }
        return state
    }
    func probeProxy() async -> ProxyProbe { ProxyProbe(healthy: healthy || healthyNodes.contains(state.selected ?? ""), latencyMS: 100) }
    func delay(node: String, endpoint: String, url: String) async throws -> Int {
        tested.append(node); testedURLs.append(url)
        activeTests += 1; maxConcurrentTests = max(maxConcurrentTests, activeTests)
        defer { activeTests -= 1 }
        if slowTests { try await Task.sleep(nanoseconds: 5_000_000_000) }
        else { try await Task.sleep(nanoseconds: 2_000_000) }
        if let change = changeDuringTesting { state = change; changeDuringTesting = nil }
        let values = url == LiveClashService.testURLs[1] ? fallbackDelays ?? delays : delays
        guard let result = values[node] else { throw HerdrError("timeout") }
        return result
    }
    func select(node: String, snapshot: ClashSnapshot) async throws {
        try Task.checkCancellation()
        selected.append(node)
        state = clashSnapshot(selected: node, mode: state.mode, endpoint: state.endpoint, names: state.nodes["GLOBAL"]!.all!.filter { $0 != "DIRECT" && $0 != "自动选择" })
        if selectionErrorAfterApplying { throw HerdrError("PUT timeout") }
    }
    func setHealthy(_ value: Bool) { healthy = value }
    func setState(_ value: ClashSnapshot) { state = value }
    func setDelays(_ values: [String: Int], fallback: [String: Int]? = nil) { delays = values; fallbackDelays = fallback }
    func setHealthyNodes(_ names: Set<String>) { healthyNodes = names }
    func setSnapshotFailure(_ value: Bool) { failSnapshot = value }
    func setMutation(_ value: ClashSnapshot) { changeDuringTesting = value }
    func setSlowTests() { slowTests = true }
    func setSelectionError() { selectionErrorAfterApplying = true }
}

final class ClashPolicyTests: XCTestCase {
    func testJapaneseLeavesOnly() {
        for name in [japanFast, "Japan 01", "JP-02", "Tokyo JP", "日本节点"] { XCTAssertTrue(ClashNode(name: name).isJapaneseLeaf, name) }
        for name in [otherNode, "SG-01", "notjp", "jpservice", "Tokyo", "剩余流量"] { XCTAssertFalse(ClashNode(name: name).isJapaneseLeaf, name) }
        for type in ["Selector", "URLTest", "Fallback", "LoadBalance", "Relay", "DIRECT", "REJECT"] {
            XCTAssertFalse(ClashNode(name: japanFast, type: type).isJapaneseLeaf)
        }
        XCTAssertFalse(ClashNode(name: japanFast, all: [otherNode]).isJapaneseLeaf)
        XCTAssertEqual(clashSnapshot().candidates.count, 2)
    }
    func testRankingIgnoresOtherRegionsAndInvalidDelays() {
        let ranked = ProxyRecoveryPolicy.ranked([NodeDelay(name: otherNode, milliseconds: 1), NodeDelay(name: japanSlow, milliseconds: 100), NodeDelay(name: japanFast, milliseconds: 20), NodeDelay(name: "JP zero", milliseconds: 0), NodeDelay(name: "JP timeout", milliseconds: 6000)], allowed: [japanFast, japanSlow, "JP zero", "JP timeout"])
        XCTAssertEqual(ranked.map(\.name), [japanFast, japanSlow])
    }
    func testConsecutiveFailuresAndFreshHealth() {
        var policy = ProxyRecoveryPolicy()
        policy.observe(healthy: false); XCTAssertFalse(policy.shouldRecover)
        policy.observe(healthy: true); policy.observe(healthy: false); XCTAssertFalse(policy.shouldRecover)
        policy.observe(healthy: false); XCTAssertTrue(policy.shouldRecover)
        var status = ProxyStatus(); status.phase = .healthy; status.healthyAt = 100
        XCTAssertTrue(status.permitsAgent(at: 120)); XCTAssertFalse(status.permitsAgent(at: 120.1))
        XCTAssertFalse(status.permitsAgent(at: 99))
    }
    func testSnapshotDetectsManualChangeAndRestart() {
        XCTAssertFalse(clashSnapshot().hasSameSelectionContext(as: clashSnapshot(selected: japanFast)))
        XCTAssertFalse(clashSnapshot().hasSameSelectionContext(as: clashSnapshot(endpoint: "/tmp/new.sock")))
        XCTAssertFalse(clashSnapshot(mode: "rule").canSelect)
    }
}

@MainActor
final class ProxyMonitorTests: XCTestCase {
    func testHealthyNonJapaneseNodeIsPreserved() async {
        let service = FakeClash()
        let proxy = ProxyMonitor(service: service); proxy.start(automatic: false)
        await proxy.checkNow()
        XCTAssertTrue(proxy.permitsAgent); XCTAssertEqual(proxy.status.selected, otherNode)
        let selected = await service.selected, tested = await service.tested
        XCTAssertTrue(selected.isEmpty); XCTAssertTrue(tested.isEmpty)
        proxy.stop()
    }
    func testTwoFailedRoundsSelectFastestJapaneseNode() async {
        let service = FakeClash(); await service.setHealthy(false); await service.setHealthyNodes([japanFast])
        let proxy = ProxyMonitor(service: service); proxy.start(automatic: false)
        await proxy.checkNow()
        XCTAssertFalse(proxy.permitsAgent); XCTAssertEqual(proxy.status.phase, .suspect)
        let first = await service.selected; XCTAssertTrue(first.isEmpty)
        await proxy.checkNow()
        let selected = await service.selected, tested = await service.tested
        XCTAssertEqual(selected, [japanFast]); XCTAssertTrue(Set(tested).isSubset(of: [japanFast, japanSlow]))
        XCTAssertTrue(proxy.permitsAgent); proxy.stop()
    }
    func testVerificationFailureTriesNextJapaneseNode() async {
        let service = FakeClash(); await service.setHealthy(false); await service.setHealthyNodes([japanSlow])
        let proxy = ProxyMonitor(service: service); proxy.start(automatic: false)
        await proxy.checkNow(); await proxy.checkNow()
        let selected = await service.selected
        XCTAssertEqual(selected, [japanFast, japanSlow]); XCTAssertTrue(proxy.permitsAgent); proxy.stop()
    }
    func testAllTimeoutsRetryWithoutLimitAndWithoutFallbackCountry() async {
        let service = FakeClash(); await service.setHealthy(false); await service.setDelays([:])
        var now = 0.0
        let proxy = ProxyMonitor(service: service, clock: { now }); proxy.start(automatic: false)
        await proxy.checkNow(); now = 10; await proxy.checkNow()
        XCTAssertEqual(proxy.status.phase, .retrying); XCTAssertEqual(proxy.status.nextCheck, 20)
        for _ in 0..<6 { now += 10; await proxy.checkNow() }
        let selected = await service.selected, tested = await service.tested
        XCTAssertTrue(selected.isEmpty); XCTAssertEqual(proxy.status.round, 7)
        XCTAssertEqual(tested.count, 7 * 2 * 2); XCTAssertFalse(proxy.permitsAgent); proxy.stop()
    }
    func testBackupURLIsUsedWhenPrimaryAllFail() async {
        let service = FakeClash(); await service.setHealthy(false); await service.setHealthyNodes([japanFast])
        await service.setDelays([:], fallback: [japanFast: 50, japanSlow: 100])
        let proxy = ProxyMonitor(service: service); proxy.start(automatic: false)
        await proxy.checkNow(); await proxy.checkNow()
        XCTAssertTrue(proxy.permitsAgent)
        let urls = await service.testedURLs; XCTAssertEqual(Set(urls), Set(LiveClashService.testURLs)); proxy.stop()
    }
    func testNoJapaneseNodesAndRuleModeNeverSelect() async {
        for state in [clashSnapshot(names: [otherNode]), clashSnapshot(mode: "rule")] {
            let service = FakeClash(); await service.setHealthy(false); await service.setState(state)
            let proxy = ProxyMonitor(service: service); proxy.start(automatic: false)
            await proxy.checkNow(); await proxy.checkNow()
            let selected = await service.selected; XCTAssertTrue(selected.isEmpty); XCTAssertFalse(proxy.permitsAgent); proxy.stop()
        }
    }
    func testManualSelectionOrReloadInvalidatesMeasurements() async {
        for state in [clashSnapshot(selected: japanSlow), clashSnapshot(endpoint: "/tmp/restarted.sock"), clashSnapshot(names: [japanSlow, otherNode])] {
            let service = FakeClash(); await service.setHealthy(false); await service.setMutation(state)
            let proxy = ProxyMonitor(service: service); proxy.start(automatic: false)
            await proxy.checkNow(); await proxy.checkNow()
            let selected = await service.selected; XCTAssertTrue(selected.isEmpty); XCTAssertEqual(proxy.status.phase, .retrying); proxy.stop()
        }
    }
    func testAmbiguousWriteUsesReadbackInsteadOfRepeating() async {
        let service = FakeClash(); await service.setHealthy(false); await service.setHealthyNodes([japanFast]); await service.setSelectionError()
        let proxy = ProxyMonitor(service: service); proxy.start(automatic: false)
        await proxy.checkNow(); await proxy.checkNow()
        let selected = await service.selected; XCTAssertEqual(selected, [japanFast]); XCTAssertTrue(proxy.permitsAgent); proxy.stop()
    }
    func testControllerUnavailableDoesNotPreventHealthyProxyUse() async {
        let service = FakeClash(); await service.setSnapshotFailure(true)
        let proxy = ProxyMonitor(service: service); proxy.start(automatic: false)
        await proxy.checkNow(); XCTAssertTrue(proxy.permitsAgent)
        await service.setHealthy(false); await proxy.checkNow(); await proxy.checkNow()
        XCTAssertEqual(proxy.status.phase, .unavailable); XCTAssertFalse(proxy.permitsAgent)
        await service.setSnapshotFailure(false); await service.setHealthy(true); await proxy.checkNow()
        XCTAssertTrue(proxy.permitsAgent); proxy.stop()
    }
    func testStopCancelsSlowTestsAndPreventsSelection() async throws {
        let service = FakeClash(); await service.setHealthy(false); await service.setSlowTests()
        let proxy = ProxyMonitor(service: service, interval: 0.01); proxy.start()
        try await Task.sleep(nanoseconds: 60_000_000)
        proxy.stop()
        try await Task.sleep(nanoseconds: 50_000_000)
        let selected = await service.selected, active = await service.activeTests
        XCTAssertTrue(selected.isEmpty); XCTAssertEqual(active, 0); XCTAssertEqual(proxy.status.phase, .stopped)
    }
    func testAtMostFourConcurrentMeasurements() async {
        let service = FakeClash(); await service.setHealthy(false)
        let names = (1...11).map { "JP-\($0)" }
        await service.setState(clashSnapshot(names: names)); await service.setDelays([:])
        let proxy = ProxyMonitor(service: service); proxy.start(automatic: false)
        await proxy.checkNow(); await proxy.checkNow()
        let concurrent = await service.maxConcurrentTests; XCTAssertLessThanOrEqual(concurrent, 4); XCTAssertGreaterThan(concurrent, 1); proxy.stop()
    }
}
