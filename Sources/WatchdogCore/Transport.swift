import Foundation
import Darwin

public struct HerdrError: LocalizedError, Sendable {
    public let message: String
    public let definitelyNotSent: Bool
    public var errorDescription: String? { message }
    public init(_ message: String, definitelyNotSent: Bool = true) {
        self.message = message; self.definitelyNotSent = definitelyNotSent
    }
}

private final class SocketCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false
    private var fd: Int32 = -1
    func register(_ fd: Int32) throws {
        lock.lock(); defer { lock.unlock() }
        guard !cancelled else { throw CancellationError() }
        self.fd = fd
    }
    func check() throws {
        lock.lock(); defer { lock.unlock() }
        if cancelled { throw CancellationError() }
    }
    func write(_ data: UnsafeRawBufferPointer, offset: Int) throws -> Int {
        lock.lock(); defer { lock.unlock() }
        if cancelled { throw CancellationError() }
        return Darwin.send(fd, data.baseAddress!.advanced(by: offset), data.count - offset, 0)
    }
    func cancel() {
        lock.lock(); defer { lock.unlock() }
        cancelled = true
        if fd >= 0 { _ = Darwin.shutdown(fd, SHUT_RDWR) }
    }
    func close() {
        lock.lock(); defer { lock.unlock() }
        if fd >= 0 { Darwin.close(fd); fd = -1 }
    }
}

public struct SocketRPC: Sendable {
    public var timeout: TimeInterval = 3
    public init(timeout: TimeInterval = 3) { self.timeout = timeout }

    public func call(socketPath: String, method: String, params: [String: String] = [:], waitForWorking: Bool = false) async throws -> Data {
        let id = UUID().uuidString
        var parameters: [String: Any] = params
        if waitForWorking { parameters["wait"] = ["until": ["working"], "timeout_ms": 5000] }
        var payload = try JSONSerialization.data(withJSONObject: ["id": id, "method": method, "params": parameters])
        payload.append(10)
        let cancellation = SocketCancellation()
        let request = payload
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await Task.detached(priority: .utility) {
                try exchange(path: socketPath, request: request, id: id, cancellation: cancellation)
            }.value
        } onCancel: { cancellation.cancel() }
    }

    private func exchange(path: String, request: Data, id: String, cancellation: SocketCancellation) throws -> Data {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw HerdrError("无法创建本地连接") }
        do { try cancellation.register(fd) } catch { Darwin.close(fd); throw error }
        defer { cancellation.close() }
        var noSignal: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &noSignal, socklen_t(MemoryLayout<Int32>.size))
        _ = fcntl(fd, F_SETFL, O_NONBLOCK)
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        let pathBytes = Array(path.utf8CString)
        guard pathBytes.count <= MemoryLayout.size(ofValue: address.sun_path) else { throw HerdrError("Herdr socket 路径过长") }
        withUnsafeMutableBytes(of: &address.sun_path) { buffer in
            pathBytes.withUnsafeBytes { source in buffer.copyBytes(from: source) }
        }
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        let connected = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) }
        }
        if connected < 0 && errno != EINPROGRESS { throw HerdrError("Herdr 未连接：\(String(cString: strerror(errno)))") }
        try wait(fd, events: Int16(POLLOUT), deadline: deadline, cancellation: cancellation, sent: false)
        var connectionError: Int32 = 0
        var length = socklen_t(MemoryLayout<Int32>.size)
        getsockopt(fd, SOL_SOCKET, SO_ERROR, &connectionError, &length)
        guard connectionError == 0 else { throw HerdrError("Herdr 连接失败") }
        var sent = 0
        while sent < request.count {
            try wait(fd, events: Int16(POLLOUT), deadline: deadline, cancellation: cancellation, sent: sent > 0)
            let count = try request.withUnsafeBytes { try cancellation.write($0, offset: sent) }
            if count > 0 { sent += count }
            else if errno != EAGAIN && errno != EINTR { throw HerdrError("Herdr 写入失败", definitelyNotSent: sent == 0) }
        }
        var response = Data()
        var buffer = [UInt8](repeating: 0, count: 16384)
        while !response.contains(10) {
            try wait(fd, events: Int16(POLLIN), deadline: deadline, cancellation: cancellation, sent: true)
            let count = recv(fd, &buffer, buffer.count, 0)
            if count > 0 { response.append(contentsOf: buffer.prefix(count)) }
            else if count == 0 { throw HerdrError("Herdr 连接已关闭", definitelyNotSent: false) }
            else if errno != EAGAIN && errno != EINTR { throw HerdrError("Herdr 读取失败", definitelyNotSent: false) }
            guard response.count < 4 * 1024 * 1024 else { throw HerdrError("Herdr 响应过大", definitelyNotSent: false) }
        }
        guard let end = response.firstIndex(of: 10),
              let envelope = try? JSONSerialization.jsonObject(with: response[..<end]) as? [String: Any],
              envelope["id"] as? String == id else { throw HerdrError("Herdr 响应无法验证", definitelyNotSent: false) }
        if let error = envelope["error"] as? [String: Any] {
            let code = error["code"] as? String ?? "error"
            let refused = ["agent_blocked", "agent_not_found", "agent_not_ready", "invalid_params", "method_not_found"].contains(code)
            throw HerdrError("\(code)：\(error["message"] as? String ?? "请求失败")", definitelyNotSent: refused)
        }
        guard let result = envelope["result"] else { throw HerdrError("Herdr 缺少响应结果", definitelyNotSent: false) }
        return try JSONSerialization.data(withJSONObject: result)
    }

    private func wait(_ fd: Int32, events: Int16, deadline: TimeInterval, cancellation: SocketCancellation, sent: Bool) throws {
        while true {
            try cancellation.check()
            let remaining = deadline - ProcessInfo.processInfo.systemUptime
            guard remaining > 0 else { throw HerdrError("Herdr 请求超时", definitelyNotSent: !sent) }
            var descriptor = pollfd(fd: fd, events: events, revents: 0)
            let result = poll(&descriptor, 1, Int32(min(remaining * 1000, 100)))
            if result > 0 {
                if descriptor.revents & events != 0 { return }
                throw HerdrError("Herdr 连接中断", definitelyNotSent: !sent)
            }
            if result < 0 && errno != EINTR { throw HerdrError("Herdr 连接检查失败", definitelyNotSent: !sent) }
        }
    }
}

private final class ProcessCancellation: @unchecked Sendable {
    let lock = NSLock()
    var cancelled = false
    var process: Process?
    func cancel() {
        lock.lock(); defer { lock.unlock() }
        cancelled = true
        if let process, process.isRunning { process.terminate() }
    }
    func launch(_ process: Process) throws {
        lock.lock(); defer { lock.unlock() }
        if cancelled { throw CancellationError() }
        self.process = process
        try process.run()
    }
}

public struct SessionDiscovery: Sendable {
    public let binary: String
    public init(binary: String? = nil) {
        self.binary = binary ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".local/bin/herdr").path
    }
    public func sessions() async throws -> [Session] {
        let cancellation = ProcessCancellation()
        let executable = binary
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await Task.detached(priority: .utility) {
                guard FileManager.default.isExecutableFile(atPath: executable) else { throw HerdrError("未找到 Herdr：\(executable)") }
                let process = Process()
                let pipe = Pipe()
                process.executableURL = URL(fileURLWithPath: executable)
                process.arguments = ["session", "list", "--json"]
                process.standardOutput = pipe; process.standardError = pipe
                process.standardInput = FileHandle.nullDevice
                var environment = ProcessInfo.processInfo.environment
                for key in ["HERDR_SESSION", "HERDR_SOCKET_PATH", "HERDR_PANE_ID", "HERDR_WORKSPACE_ID", "HERDR_TAB_ID"] { environment.removeValue(forKey: key) }
                process.environment = environment
                try cancellation.launch(process)
                let timeout = DispatchWorkItem { cancellation.cancel() }
                DispatchQueue.global().asyncAfter(deadline: .now() + 3, execute: timeout)
                defer { timeout.cancel() }
                let data = pipe.fileHandleForReading.readDataToEndOfFile()
                process.waitUntilExit()
                guard process.terminationStatus == 0 else { throw HerdrError("Herdr 会话发现失败或超时") }
                struct Sessions: Decodable { let sessions: [Session] }
                return try JSONDecoder().decode(Sessions.self, from: data).sessions.filter(\.running)
            }.value
        } onCancel: { cancellation.cancel() }
    }
}
