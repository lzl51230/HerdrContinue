import XCTest
import Foundation
import Darwin
@testable import WatchdogCore

/// An isolated Unix HTTP controller exercises the production curl adapter without touching Clash.
private final class ClashHTTPFixture: @unchecked Sendable {
    let directory = "/tmp/cq-\(UUID().uuidString.prefix(8))"
    let listener: Int32
    let done = DispatchSemaphore(value: 0)
    let lock = NSLock()
    var stopped = false
    var selectedName = otherNode
    var calls: [(String, String, Data)] = []
    var path: String { directory + "/mihomo-party-\(getuid())-123.sock" }
    var recorded: [(String, String, Data)] { lock.lock(); defer { lock.unlock() }; return calls }

    init() throws {
        listener = socket(AF_UNIX, SOCK_STREAM, 0)
        try FileManager.default.createDirectory(atPath: directory, withIntermediateDirectories: true)
        var address = sockaddr_un(); address.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8CString)
        withUnsafeMutableBytes(of: &address.sun_path) { destination in bytes.withUnsafeBytes { destination.copyBytes(from: $0) } }
        let bound = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(listener, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) } }
        guard bound == 0, listen(listener, 8) == 0 else { throw HerdrError("HTTP fixture failed to listen") }
        DispatchQueue.global().async { [self] in
            defer { done.signal() }
            while true {
                lock.lock(); let shouldStop = stopped; lock.unlock()
                if shouldStop { break }
                let client = accept(listener, nil, nil)
                if client < 0 { break }
                serve(client)
                Darwin.close(client)
            }
        }
    }
    private func serve(_ client: Int32) {
        var timeout = timeval(tv_sec: 2, tv_usec: 0)
        setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        var noSignal: Int32 = 1
        setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &noSignal, socklen_t(MemoryLayout<Int32>.size))
        var data = Data(), buffer = [UInt8](repeating: 0, count: 8192)
        let separator = Data("\r\n\r\n".utf8)
        var headerRange: Range<Data.Index>?
        while headerRange == nil {
            let count = recv(client, &buffer, buffer.count, 0)
            guard count > 0 else { return }
            data.append(contentsOf: buffer.prefix(count))
            headerRange = data.range(of: separator)
        }
        let range = headerRange!, headers = String(decoding: data[..<range.lowerBound], as: UTF8.self)
        let lines = headers.components(separatedBy: "\r\n")
        let first = lines[0].split(separator: " ")
        guard first.count >= 2 else { return }
        let method = String(first[0]), target = String(first[1])
        let length = lines.first(where: { $0.lowercased().hasPrefix("content-length:") })
            .flatMap { Int($0.split(separator: ":", maxSplits: 1)[1].trimmingCharacters(in: .whitespaces)) } ?? 0
        while data.count - range.upperBound < length {
            let count = recv(client, &buffer, buffer.count, 0)
            guard count > 0 else { return }
            data.append(contentsOf: buffer.prefix(count))
        }
        let body = Data(data[range.upperBound...])
        lock.lock(); calls.append((method, target, body)); lock.unlock()
        var status = 200
        let response: [String: Any]
        switch (method, target) {
        case ("GET", "/version"): response = ["meta": true, "version": "test"]
        case ("GET", "/configs"): response = ["mode": "global", "mixed-port": 7890]
        case ("GET", "/proxies"):
            let nodes = clashSnapshot(selected: selectedName).nodes
            response = ["proxies": try! JSONSerialization.jsonObject(with: JSONEncoder().encode(nodes))]
        case ("PUT", "/proxies/GLOBAL"):
            let payload = (try? JSONSerialization.jsonObject(with: body)) as? [String: String]
            selectedName = payload?["name"] ?? "invalid"
            status = 204; response = [:]
        default:
            if method == "GET", target.contains("/delay?") { response = ["delay": 42] }
            else { status = 404; response = [:] }
        }
        let payload = status == 204 ? Data() : try! JSONSerialization.data(withJSONObject: response)
        var reply = Data("HTTP/1.1 \(status) OK\r\nContent-Type: application/json\r\nContent-Length: \(payload.count)\r\nConnection: close\r\n\r\n".utf8)
        reply.append(payload)
        var written = 0
        while written < reply.count {
            let count = reply.withUnsafeBytes { Darwin.send(client, $0.baseAddress!.advanced(by: written), $0.count - written, 0) }
            if count <= 0 { break }; written += count
        }
    }
    func stop() {
        lock.lock(); stopped = true; lock.unlock()
        shutdown(listener, SHUT_RDWR); Darwin.close(listener)
        _ = done.wait(timeout: .now() + 3)
        try? FileManager.default.removeItem(atPath: directory)
    }
}

final class ClashHTTPTests: XCTestCase {
    func testDiscoveryEncodedDelayAndSelectionReadback() async throws {
        let fixture = try ClashHTTPFixture(); defer { fixture.stop() }
        let service = LiveClashService(socketDirectory: fixture.directory)
        let snapshot = try await service.snapshot()
        XCTAssertEqual(snapshot.candidates.count, 2)
        XCTAssertEqual(snapshot.selected, otherNode)
        let delay = try await service.delay(node: japanFast, endpoint: snapshot.endpoint, url: LiveClashService.testURLs[0])
        XCTAssertEqual(delay, 42)
        try await service.select(node: japanFast, snapshot: snapshot)
        let after = try await service.snapshot(); XCTAssertEqual(after.selected, japanFast)
        let delayCall = try XCTUnwrap(fixture.recorded.first { $0.1.contains("/delay?") })
        let url = try XCTUnwrap(URLComponents(string: "http://localhost" + delayCall.1))
        XCTAssertEqual(url.path, "/proxies/\(japanFast)/delay")
        XCTAssertEqual(url.queryItems?.first(where: { $0.name == "expected" })?.value, "204")
        let writes = fixture.recorded.filter { $0.0 == "PUT" }
        XCTAssertEqual(writes.count, 1)
        XCTAssertEqual(try JSONDecoder().decode([String: String].self, from: writes[0].2), ["name": japanFast])
    }
    func testNonJapaneseAndGroupSelectionsAreRefusedBeforeHTTP() async throws {
        let fixture = try ClashHTTPFixture(); defer { fixture.stop() }
        let service = LiveClashService(socketDirectory: fixture.directory)
        let snapshot = try await service.snapshot()
        for name in [otherNode, "DIRECT", "自动选择"] {
            do { try await service.select(node: name, snapshot: snapshot); XCTFail("Must refuse \(name)") }
            catch { }
        }
        XCTAssertFalse(fixture.recorded.contains { $0.0 == "PUT" })
    }
    func testControllerIsRediscoveredAfterRestart() async throws {
        let first = try ClashHTTPFixture()
        let service = LiveClashService(socketDirectory: first.directory)
        let before = try await service.snapshot(); XCTAssertEqual(before.endpoint, first.path)
        first.stop()
        do { _ = try await service.snapshot(); XCTFail("Old endpoint is gone") } catch { }
        let next = try ClashHTTPFixture(); defer { next.stop() }
        let after = try await LiveClashService(socketDirectory: next.directory).snapshot()
        XCTAssertNotEqual(after.endpoint, before.endpoint)
    }
}
