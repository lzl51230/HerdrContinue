import XCTest
@testable import WatchdogCore

final class RecoveryTests: XCTestCase {
    func testTenSecondBoundaryAndNoDuplicateClaim() throws {
        var machine = RecoveryMachine()
        XCTAssertNil(machine.evaluate(observation(), now: 100).1)
        XCTAssertNil(machine.evaluate(observation(), now: 109.999).1)
        let intent = try XCTUnwrap(machine.evaluate(observation(), now: 110).1)
        XCTAssertTrue(machine.claim(intent, fresh: observation(), now: 110))
        XCTAssertFalse(machine.claim(intent, fresh: observation(), now: 110))
        XCTAssertNil(machine.evaluate(observation(), now: 500).1)
        XCTAssertNil(machine.evaluate(observation(), now: 510).1)
    }
    func testIndependentCooldowns() {
        var machine = RecoveryMachine()
        let a = observation(terminal: "a"), b = observation(terminal: "b")
        _ = machine.evaluate(a, now: 0); _ = machine.evaluate(b, now: 3)
        XCTAssertNotNil(machine.evaluate(a, now: 10).1)
        XCTAssertNil(machine.evaluate(b, now: 10).1)
        XCTAssertNotNil(machine.evaluate(b, now: 13).1)
    }
    func testRecoveryDuringCooldownCancels() {
        var machine = RecoveryMachine()
        _ = machine.evaluate(observation(), now: 0)
        _ = machine.evaluate(observation(status: "working", sequence: 2), now: 5)
        XCTAssertNil(machine.evaluate(observation(sequence: 3), now: 10).1)
        XCTAssertNotNil(machine.evaluate(observation(sequence: 3), now: 20).1)
    }
    func testClaimRejectsDraftBlockedReplacementAndPaneMove() throws {
        var machine = RecoveryMachine()
        _ = machine.evaluate(observation(), now: 0)
        let intent = try XCTUnwrap(machine.evaluate(observation(), now: 10).1)
        for fresh in [observation(text: codexScreen(prompt: "draft")), observation(status: "blocked"),
                      observation(pid: 101), observation(terminal: "replacement")] {
            XCTAssertFalse(machine.claim(intent, fresh: fresh, now: 10))
        }
        var moved = Agent(); moved.pane_id = "w2:p9"
        XCTAssertFalse(machine.claim(intent, fresh: Observation(session: testSession, agent: moved, text: codexScreen()), now: 10))
    }
    func testRepeatedSameFailureAfterWorkingRetriesWithoutLimit() throws {
        var machine = RecoveryMachine()
        for round in 0..<40 {
            let seq = UInt64(round * 2 + 1), start = Double(round * 20)
            let failure = observation(sequence: seq)
            XCTAssertNil(machine.evaluate(failure, now: start).1)
            let intent = try XCTUnwrap(machine.evaluate(failure, now: start + 10).1)
            XCTAssertTrue(machine.claim(intent, fresh: failure, now: start + 10))
            _ = machine.evaluate(observation(status: "working", sequence: seq + 1), now: start + 11)
        }
    }
    func testFastFailureBetweenPollsRearms() throws {
        var machine = RecoveryMachine()
        _ = machine.evaluate(observation(), now: 0)
        let intent = try XCTUnwrap(machine.evaluate(observation(), now: 10).1)
        XCTAssertTrue(machine.claim(intent, fresh: observation(), now: 10))
        machine.submissionConfirmed(key: observation().key)
        XCTAssertNil(machine.evaluate(observation(sequence: 3), now: 12).1)
        XCTAssertNotNil(machine.evaluate(observation(sequence: 3), now: 22).1)
    }
    func testSeenStateChangeAloneDoesNotResend() throws {
        var machine = RecoveryMachine()
        _ = machine.evaluate(observation(), now: 0)
        let intent = try XCTUnwrap(machine.evaluate(observation(), now: 10).1)
        XCTAssertTrue(machine.claim(intent, fresh: observation(), now: 10))
        // Sequence values are global server stamps, not a per-agent transition counter.
        XCTAssertNil(machine.evaluate(observation(status: "done", sequence: 200), now: 20).1)
        XCTAssertNil(machine.evaluate(observation(status: "done", sequence: 200), now: 40).1)
    }
    func testStopClearsCountdownButPreservesAmbiguousSubmission() throws {
        var machine = RecoveryMachine()
        _ = machine.evaluate(observation(), now: 0)
        machine.stop()
        XCTAssertNil(machine.evaluate(observation(), now: 20).1)
        let intent = try XCTUnwrap(machine.evaluate(observation(), now: 30).1)
        XCTAssertTrue(machine.claim(intent, fresh: observation(), now: 30))
        machine.submissionFailed(key: observation().key, definitelyNotSent: false, now: 30)
        machine.stop()
        XCTAssertNil(machine.evaluate(observation(), now: 50).1)
        XCTAssertNil(machine.evaluate(observation(), now: 100).1)
    }
    func testDefinitelyUnsentRequestRetriesAfterCooldown() throws {
        var machine = RecoveryMachine()
        _ = machine.evaluate(observation(), now: 0)
        let intent = try XCTUnwrap(machine.evaluate(observation(), now: 10).1)
        XCTAssertTrue(machine.claim(intent, fresh: observation(), now: 10))
        machine.submissionFailed(key: observation().key, definitelyNotSent: true, now: 10)
        XCTAssertNil(machine.evaluate(observation(), now: 11).1)
        XCTAssertNil(machine.evaluate(observation(), now: 20).1)
        XCTAssertNotNil(machine.evaluate(observation(), now: 21).1)
    }
    func testReconnectRestartsCooldown() {
        var machine = RecoveryMachine()
        _ = machine.evaluate(observation(), now: 0)
        machine.suspend(keys: [observation().key])
        XCTAssertNil(machine.evaluate(observation(), now: 30).1)
        XCTAssertNotNil(machine.evaluate(observation(), now: 40).1)
    }
}
