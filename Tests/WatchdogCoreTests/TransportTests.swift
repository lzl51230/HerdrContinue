import XCTest
import Foundation
import Darwin
@testable import WatchdogCore

private final class SocketFixture: @unchecked Sendable {
    enum Mode { case success, blocked, disconnect, stall }
    let path = "/tmp/watchdog-rpc-\(UUID().uuidString).sock"
    let listener: Int32
    let release = DispatchSemaphore(value: 0)
    let done = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var received: [String: Any] = [:]
    var request: [String: Any] { lock.lock(); defer { lock.unlock() }; return received }

    init(_ mode: Mode) throws {
        listener = socket(AF_UNIX, SOCK_STREAM, 0)
        var address = sockaddr_un(); address.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8CString)
        withUnsafeMutableBytes(of: &address.sun_path) { dest in bytes.withUnsafeBytes { dest.copyBytes(from: $0) } }
        let result = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(listener, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) } }
        guard result == 0, listen(listener, 1) == 0 else { throw HerdrError("Fixture could not listen") }
        DispatchQueue.global().async { [self] in
            defer { done.signal() }
            let client = accept(listener, nil, nil)
            guard client >= 0 else { return }
            defer { Darwin.close(client) }
            var noSignal: Int32 = 1
            setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &noSignal, socklen_t(MemoryLayout<Int32>.size))
            var buffer = [UInt8](repeating: 0, count: 8192)
            var data = Data()
            while !data.contains(10) {
                let count = recv(client, &buffer, buffer.count, 0)
                guard count > 0 else { return }
                data.append(contentsOf: buffer.prefix(count))
            }
            guard let request = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
            lock.lock(); received = request; lock.unlock()
            if mode == .disconnect { return }
            if mode == .stall { _ = release.wait(timeout: .now() + 2); return }
            let envelope: [String: Any]
            if mode == .blocked { envelope = ["id": request["id"]!, "error": ["code": "agent_blocked", "message": "waiting for approval"]] }
            else { envelope = ["id": request["id"]!, "result": ["type": "agent_info"]] }
            var response = try! JSONSerialization.data(withJSONObject: envelope); response.append(10)
            let split = response.count / 2
            _ = response.prefix(split).withUnsafeBytes { Darwin.send(client, $0.baseAddress, $0.count, 0) }
            Thread.sleep(forTimeInterval: 0.01)
            _ = response.suffix(from: split).withUnsafeBytes { Darwin.send(client, $0.baseAddress, $0.count, 0) }
        }
    }
    func stop() {
        release.signal()
        _ = shutdown(listener, SHUT_RDWR)
        Darwin.close(listener)
        _ = done.wait(timeout: .now() + 2)
        unlink(path)
    }
}

final class TransportTests: XCTestCase {
    func testPromptWireFormatAndFragmentedResponse() async throws {
        let server = try SocketFixture(.success); defer { server.stop() }
        let data = try await SocketRPC().call(socketPath: server.path, method: "agent.prompt", params: ["target": "w1:p1", "text": "继续"], waitForWorking: true)
        XCTAssertTrue(String(data: data, encoding: .utf8)!.contains("agent_info"))
        let params = try XCTUnwrap(server.request["params"] as? [String: Any])
        XCTAssertEqual(params["text"] as? String, "继续")
        XCTAssertEqual(params["target"] as? String, "w1:p1")
        let wait = try XCTUnwrap(params["wait"] as? [String: Any])
        XCTAssertEqual(wait["until"] as? [String], ["working"])
        XCTAssertEqual(wait["timeout_ms"] as? Int, 5000)
    }
    func testBlockedIsDefiniteRefusal() async throws {
        let server = try SocketFixture(.blocked); defer { server.stop() }
        do { _ = try await SocketRPC().call(socketPath: server.path, method: "agent.prompt"); XCTFail("Expected refusal") }
        catch let error as HerdrError { XCTAssertTrue(error.definitelyNotSent) }
    }
    func testDisconnectAfterWriteIsAmbiguous() async throws {
        let server = try SocketFixture(.disconnect); defer { server.stop() }
        do { _ = try await SocketRPC().call(socketPath: server.path, method: "agent.prompt"); XCTFail("Expected disconnect") }
        catch let error as HerdrError { XCTAssertFalse(error.definitelyNotSent) }
    }
    func testDeadlineAfterWriteIsAmbiguous() async throws {
        let server = try SocketFixture(.stall); defer { server.stop() }
        do { _ = try await SocketRPC(timeout: 0.1).call(socketPath: server.path, method: "agent.prompt"); XCTFail("Expected timeout") }
        catch let error as HerdrError { XCTAssertFalse(error.definitelyNotSent) }
    }
    func testCancellationInterruptsPendingRead() async throws {
        let server = try SocketFixture(.stall); defer { server.stop() }
        let task = Task { try await SocketRPC(timeout: 3).call(socketPath: server.path, method: "agent.prompt") }
        try await Task.sleep(nanoseconds: 50_000_000)
        let started = ProcessInfo.processInfo.systemUptime
        task.cancel()
        do { _ = try await task.value; XCTFail("Expected cancellation") }
        catch { /* cancellation or the shutdown-induced transport error both prevent further input */ }
        XCTAssertLessThan(ProcessInfo.processInfo.systemUptime - started, 0.5)
        XCTAssertNotNil(server.request["method"])
    }
    func testUnavailableSocketIsDefinitelyUnsent() async throws {
        do { _ = try await SocketRPC().call(socketPath: "/tmp/watchdog-not-present-\(UUID()).sock", method: "agent.prompt"); XCTFail("Expected connection failure") }
        catch let error as HerdrError { XCTAssertTrue(error.definitelyNotSent) }
    }
}
