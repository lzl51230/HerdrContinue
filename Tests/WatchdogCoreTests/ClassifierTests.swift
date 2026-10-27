import XCTest
@testable import WatchdogCore

let testSession = Session(name: "test", socket_path: "/tmp/test.sock")
let capacity = "Selected model is at capacity. Please try a different model."

func codexScreen(error: String = capacity, prompt: String = "Ask Codex to do anything", prefix: String = "› Build a feature\n") -> String {
    prefix + "\n■ " + error + "\n\n› " + prompt + "\n\n  GPT-6.1-Sol high · ~/project\n  ? for shortcuts\n"
}
func observation(text: String = codexScreen(), status: String = "idle", sequence: UInt64 = 1,
                 terminal: String = "term_test", pid: Int? = 100) -> Observation {
    Observation(session: testSession, agent: Agent(status: status, terminal: terminal, sequence: sequence), text: text, foregroundPID: pid)
}

final class ClassifierTests: XCTestCase {
    let classifier = FailureClassifier()

    func testRealCapacityMessage() {
        guard case .failure(let evidence) = classifier.assess(observation()) else { return XCTFail("Expected failure") }
        XCTAssertEqual(evidence.reason, .capacity)
    }
    func testNetworkMessages() {
        for error in ["stream disconnected before completion: error sending request", "Request timed out", "Connection reset by peer", "fetch failed", "网络连接失败", "unexpected status 502 Bad Gateway", "504 Gateway Timeout", "Connection timed out"] {
            guard case .failure(let evidence) = classifier.assess(observation(text: codexScreen(error: error))) else { XCTFail(error); continue }
            XCTAssertEqual(evidence.reason, .network)
        }
    }
    func testExcludedFailures() {
        for error in ["Conversation interrupted - use /feedback if something went wrong", "Context window exceeded", "Usage limit reached",
                      "insufficient_quota: network error", "rate limit exceeded", "Authentication failed: connection error", "Request canceled"] {
            if case .failure = classifier.assess(observation(text: codexScreen(error: error))) { XCTFail(error) }
        }
    }
    func testNonReadyStatesNeverPrompt() {
        for state in ["working", "blocked", "unknown"] {
            if case .failure = classifier.assess(observation(status: state)) { XCTFail(state) }
        }
    }
    func testNormalCompletionAndHistoricalError() {
        let screen = "■ \(capacity)\n› 继续\n• Completed successfully.\n› Ask Codex to do anything\n  GPT-6 high\n"
        if case .failure = classifier.assess(observation(text: screen)) { XCTFail("Historical error must not trigger") }
    }
    func testLatestErrorWins() {
        let screen = "■ \(capacity)\n› 继续\n■ Conversation interrupted\n› Ask Codex to do anything\n"
        if case .failure = classifier.assess(observation(text: screen)) { XCTFail("Latest error is manual interruption") }
    }
    func testToolOutputAndQuotedErrorAreIgnored() {
        for prefix in ["  ■ ", "│ ■ ", "> ■ ", "  └ ■ "] {
            let screen = prefix + capacity + "\n› Ask Codex to do anything\n"
            if case .failure = classifier.assess(observation(text: screen)) { XCTFail(prefix) }
        }
    }
    func testDraftIsNotOverwritten() {
        if case .failure = classifier.assess(observation(text: codexScreen(prompt: "My unfinished request"))) { XCTFail("Draft") }
    }
    func testMultilineDraftIsNotOverwritten() {
        let screen = "■ \(capacity)\n› \n  second draft line\n  GPT-6 high\n"
        if case .failure = classifier.assess(observation(text: screen)) { XCTFail("Wrapped draft") }
    }
    func testMissingPromptAndUnknownAgentAreSkipped() {
        if case .failure = classifier.assess(observation(text: "■ " + capacity)) { XCTFail("No prompt") }
        let obs = Observation(session: testSession, agent: Agent(kind: "unknown"), text: codexScreen())
        if case .failure = classifier.assess(obs) { XCTFail("Unknown adapter") }
    }
    func testMissingIdentityIsSkipped() {
        if case .failure = classifier.assess(observation(pid: nil)) { XCTFail("No PID") }
    }
    func testOMPRequiresCurrentStructuredErrorAndEmptyEditor() throws {
        let record: [String: Any] = ["type": "message", "id": "error1", "message": ["role": "assistant", "stopReason": "error", "errorMessage": "Connection reset by peer"]]
        let transcript = String(data: try JSONSerialization.data(withJSONObject: record), encoding: .utf8)!
        let screen = "Error: Connection reset by peer\n π > model\n╰─\n"
        let obs = Observation(session: testSession, agent: Agent(kind: "omp"), text: screen, transcript: transcript)
        guard case .failure(let evidence) = classifier.assess(obs) else { return XCTFail("OMP error") }
        XCTAssertEqual(evidence.reason, .network)
        for changed in [screen + "unfinished input", "π > model\n╰─"] {
            if case .failure = classifier.assess(Observation(session: testSession, agent: Agent(kind: "omp"), text: changed, transcript: transcript)) { XCTFail("Mismatch/draft") }
        }
        let newerUser = transcript + "\n{\"type\":\"message\",\"id\":\"user2\",\"message\":{\"role\":\"user\"}}"
        if case .failure = classifier.assess(Observation(session: testSession, agent: Agent(kind: "omp"), text: screen, transcript: newerUser)) { XCTFail("New user message") }
    }
    func testWrappedNativeErrorAndANSI() {
        let screen = "\u{001B}[31m■ stream disconnected before completion:\u{001B}[0m\n  error sending request\n\n› Ask Codex to do anything\n"
        guard case .failure = classifier.assess(observation(text: screen)) else { return XCTFail("Wrapped error") }
    }
}
