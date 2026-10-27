import Foundation
import Darwin

struct CurlResponse: Sendable { let status: Int; let body: Data }

private final class CurlCancellation: @unchecked Sendable {
    let lock = NSLock()
    var process: Process?
    var cancelled = false
    func launch(_ process: Process) throws {
        lock.lock(); defer { lock.unlock() }
        guard !cancelled else { throw CancellationError() }
        self.process = process
        try process.run()
    }
    func check() throws {
        lock.lock(); defer { lock.unlock() }
        if cancelled { throw CancellationError() }
    }
    func cancel() {
        lock.lock(); defer { lock.unlock() }
        cancelled = true
        guard let process, process.isRunning else { return }
        process.terminate()
        DispatchQueue.global().asyncAfter(deadline: .now() + 0.3) {
            if process.isRunning { kill(process.processIdentifier, SIGKILL) }
        }
    }
}

/// curl supplies HTTP framing, TLS verification, CONNECT tunneling and Unix socket support.
/// Arguments are passed directly to Process, never through a shell. User curl configuration is disabled.
struct CurlHTTP: Sendable {
    func request(_ arguments: [String], timeout: Double) async throws -> CurlResponse {
        let cancellation = CurlCancellation()
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await Task.detached(priority: .utility) {
                let process = Process(), output = Pipe()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/curl")
                process.arguments = ["--disable", "--silent", "--globoff", "--connect-timeout", "3", "--max-time", String(timeout),
                                     "--proto", "=http,https", "--write-out", "\n__WATCHDOG_HTTP__%{http_code}"] + arguments
                var environment = ProcessInfo.processInfo.environment
                for key in environment.keys where ["http_proxy", "https_proxy", "all_proxy", "no_proxy"].contains(key.lowercased()) { environment.removeValue(forKey: key) }
                process.environment = environment
                process.standardInput = FileHandle.nullDevice
                process.standardOutput = output; process.standardError = FileHandle.nullDevice
                try cancellation.launch(process)
                let watchdog = DispatchWorkItem { cancellation.cancel() }
                DispatchQueue.global().asyncAfter(deadline: .now() + timeout + 1, execute: watchdog)
                defer { watchdog.cancel() }
                var data = Data()
                while let chunk = try output.fileHandleForReading.read(upToCount: 65536), !chunk.isEmpty {
                    data.append(chunk)
                    if data.count > 4 * 1024 * 1024 { cancellation.cancel(); throw HerdrError("Clash 响应过大") }
                }
                process.waitUntilExit()
                try cancellation.check()
                guard process.terminationStatus == 0 else { throw HerdrError("网络请求失败（curl \(process.terminationStatus)）") }
                let marker = Data("\n__WATCHDOG_HTTP__".utf8)
                guard let range = data.range(of: marker, options: .backwards),
                      let status = Int(String(decoding: data[range.upperBound...], as: UTF8.self)) else { throw HerdrError("Clash 响应格式错误") }
                return CurlResponse(status: status, body: Data(data[..<range.lowerBound]))
            }.value
        } onCancel: { cancellation.cancel() }
    }
}

public struct LiveClashService: ClashService {
    public static let testURLs = ["https://www.gstatic.com/generate_204", "https://cp.cloudflare.com/generate_204"]
    private let http = CurlHTTP()
    private let socketDirectory: String
    public init(socketDirectory: String = "/tmp") { self.socketDirectory = socketDirectory }

    public func snapshot() async throws -> ClashSnapshot {
        let prefix = "mihomo-party-\(getuid())-"
        let paths = (try FileManager.default.contentsOfDirectory(atPath: socketDirectory))
            .filter { $0.hasPrefix(prefix) && $0.hasSuffix(".sock") }.sorted()
        var snapshots: [ClashSnapshot] = []
        for name in paths {
            try Task.checkCancellation()
            let path = URL(fileURLWithPath: socketDirectory).appendingPathComponent(name).path
            guard let attributes = try? FileManager.default.attributesOfItem(atPath: path),
                  attributes[.type] as? FileAttributeType == .typeSocket else { continue }
            do {
                let version: Version = try await get(path, resource: "/version")
                guard version.meta else { continue }
                let config: Config = try await get(path, resource: "/configs")
                guard config.mixedPort == 7890 else { continue }
                let proxies: Proxies = try await get(path, resource: "/proxies")
                snapshots.append(ClashSnapshot(endpoint: path, mode: config.mode, mixedPort: config.mixedPort, nodes: proxies.proxies))
            } catch is CancellationError { throw CancellationError() }
            catch { continue }
        }
        guard snapshots.count == 1, let result = snapshots.first else {
            throw HerdrError(snapshots.isEmpty ? "未连接到管理 7890 端口的 Clash Party，稍后重试" : "发现多个 Clash 控制接口，暂停切换以避免误操作")
        }
        return result
    }

    public func probeProxy() async -> ProxyProbe {
        await withTaskGroup(of: ProxyProbe.self) { group in
            for url in Self.testURLs {
                group.addTask {
                    let start = ProcessInfo.processInfo.systemUptime
                    do {
                        let response = try await http.request(["--proxy", "http://127.0.0.1:7890", "--noproxy", "", "--output", "/dev/null", url], timeout: 5)
                        return ProxyProbe(healthy: response.status == 204, latencyMS: Int((ProcessInfo.processInfo.systemUptime - start) * 1000))
                    } catch { return ProxyProbe(healthy: false) }
                }
            }
            for await result in group where result.healthy { group.cancelAll(); return result }
            return ProxyProbe(healthy: false)
        }
    }

    public func delay(node: String, endpoint: String, url: String) async throws -> Int {
        var components = URLComponents()
        components.queryItems = [URLQueryItem(name: "url", value: url), URLQueryItem(name: "timeout", value: "5000"), URLQueryItem(name: "expected", value: "204")]
        let result: Delay = try await get(endpoint, resource: "/proxies/\(encoded(node))/delay?\(components.percentEncodedQuery!)", timeout: 6)
        guard result.delay > 0 && result.delay <= 5000 else { throw HerdrError("节点测速失败") }
        return result.delay
    }

    public func select(node: String, snapshot: ClashSnapshot) async throws {
        guard snapshot.canSelect, snapshot.candidates.contains(where: { $0.name == node }) else { throw HerdrError("拒绝选择非日本节点或不适用的代理组") }
        try Task.checkCancellation()
        let body = String(decoding: try JSONEncoder().encode(["name": node]), as: UTF8.self)
        let response = try await http.request(["--proxy", "", "--unix-socket", snapshot.endpoint,
                                               "--request", "PUT", "--header", "Content-Type: application/json",
                                               "--data-binary", body, "http://localhost/proxies/GLOBAL"], timeout: 3)
        guard response.status == 204 else { throw HerdrError("Clash 切换请求失败（HTTP \(response.status)）") }
    }

    private func get<T: Decodable>(_ socket: String, resource: String, timeout: Double = 3) async throws -> T {
        let response = try await http.request(["--proxy", "", "--unix-socket", socket, "http://localhost" + resource], timeout: timeout)
        guard response.status == 200 else { throw HerdrError("Clash 控制接口异常（HTTP \(response.status)）") }
        return try JSONDecoder().decode(T.self, from: response.body)
    }
    private func encoded(_ name: String) -> String {
        name.addingPercentEncoding(withAllowedCharacters: CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~")))!
    }
    private struct Version: Decodable { let meta: Bool }
    private struct Config: Decodable {
        let mode: String; let mixedPort: Int
        enum CodingKeys: String, CodingKey { case mode; case mixedPort = "mixed-port" }
    }
    private struct Proxies: Decodable { let proxies: [String: ClashNode] }
    private struct Delay: Decodable { let delay: Int }
}
