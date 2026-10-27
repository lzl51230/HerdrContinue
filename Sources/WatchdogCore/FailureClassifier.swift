import Foundation
import CryptoKit

public struct FailureClassifier: Sendable {
    public init() {}

    public func assess(_ observation: Observation) -> Assessment {
        let agent = observation.agent
        guard observation.error == nil else { return .skip("读取失败，等待重连") }
        guard agent.acceptsInput else {
            switch agent.agent_status {
            case "working": return .skip("正在运行")
            case "blocked": return .skip("等待人工确认")
            default: return .skip("状态未知或尚未就绪")
            }
        }
        guard observation.foregroundPID != nil else { return .skip("无法确认当前进程") }
        switch agent.agent {
        case "codex": return codex(observation.text)
        case "omp": return omp(observation)
        default: return .skip("原因未知：尚无此 agent 的可靠解析规则")
        }
    }

    public func reason(for message: String) -> FailureReason? {
        let lower = message.lowercased()
        // Persistent failures and user cancellation must win over incidental network words.
        let excluded = ["context window", "context length", "context_length", "maximum context",
                        "insufficient_quota", "quota exceeded", "usage limit", "credit balance",
                        "billing", "payment required", "rate limit", "rate_limit", "too many requests",
                        "unauthorized", "authentication", "invalid api key", "permission denied",
                        "conversation interrupted", "user interrupted", "cancelled", "canceled",
                        "certificate verify failed", "上下文", "额度", "余额", "认证", "手动中断"]
        guard !excluded.contains(where: lower.contains) else { return nil }
        if ["at capacity", "model capacity", "overloaded",
            "over capacity", "server is busy", "service unavailable", "模型容量不足", "模型已满",
            "服务过载", "模型繁忙"].contains(where: lower.contains) { return .capacity }
        if ["stream disconnected", "stream closed", "stream terminated", "connection reset",
            "connection refused", "connection closed", "connection error", "connection failed", "error connecting",
            "network error", "network connection", "network is unreachable", "fetch failed",
            "failed to fetch", "error sending request", "request timed out", "request timeout",
            "connect timeout", "connection timed out", "read timeout", "operation timed out", "dns", "econnreset", "econnrefused", "etimedout",
            "enotfound", "socket hang up", "tls handshake", "websocket error", "unexpected eof",
            "bad gateway", "gateway timeout", "gateway time-out", "upstream connect error",
            "网络错误", "网络连接失败", "连接中断", "连接超时", "请求超时", "流式传输失败"
        ].contains(where: lower.contains) { return .network }
        return nil
    }

    private func codex(_ text: String) -> Assessment {
        let lines = clean(text).components(separatedBy: "\n")
        guard let inputIndex = lines.lastIndex(where: { $0.trimmingCharacters(in: .whitespaces).hasPrefix("›") }) else {
            return .skip("无法确认输入框")
        }
        let input = String(lines[inputIndex].trimmingCharacters(in: .whitespaces).dropFirst()).trimmingCharacters(in: .whitespaces)
        let placeholders: Set<String> = ["", "Ask Codex to do anything", "Find and fix a bug in @filename",
                                        "Write tests for @filename", "Summarize recent commits",
                                        "Explain this codebase", "Implement {feature}", "Improve documentation in @filename"]
        guard placeholders.contains(input) else { return .skip("输入框已有草稿，暂不发送") }
        // Wrapped drafts occur between the prompt line and the model/footer line.
        let tail = lines.dropFirst(inputIndex + 1).filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        guard tail.allSatisfy({ isCodexFooter($0) }) else { return .skip("输入区域不明确，暂不发送") }
        let body = Array(lines.prefix(inputIndex))
        // Only a top-level native error block can trigger. A quote or tool-output line cannot.
        guard let errorIndex = body.lastIndex(where: { $0.hasPrefix("■ ") || $0.hasPrefix(" ■ ") }) else {
            return .skip("未发现当前容量或网络错误")
        }
        let afterError = body.dropFirst(errorIndex + 1)
        let continuation = afterError.prefix { line in
            let s = line.trimmingCharacters(in: .whitespaces)
            return s.isEmpty || (line.hasPrefix("  ") && !["•", "›", "└", "│", "```", "■"].contains(where: s.hasPrefix))
        }
        // Any subsequent substantive message invalidates the old error.
        let remainder = afterError.dropFirst(continuation.count)
        guard remainder.allSatisfy({ $0.trimmingCharacters(in: .whitespaces).isEmpty }) else {
            return .skip("错误之后已有新消息")
        }
        let message = ([body[errorIndex]] + continuation.filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty })
            .joined(separator: " ").trimmingCharacters(in: .whitespaces)
        guard let reason = reason(for: message) else { return .skip("当前停止原因不在自动续接范围") }
        let previousPrompt = body[..<errorIndex].lastIndex(where: { $0.trimmingCharacters(in: .whitespaces).hasPrefix("›") })
        let start = previousPrompt ?? max(0, errorIndex - 8)
        let evidence = body[start...errorIndex].joined(separator: "\n") + message
        return .failure(FailureEvidence(reason: reason, message: message, fingerprint: digest(evidence)))
    }

    private func omp(_ observation: Observation) -> Assessment {
        let lines = clean(observation.text).components(separatedBy: "\n")
        // OMP's compact editor is below its π > status line and starts with ╰─.
        guard let footer = lines.lastIndex(where: { $0.trimmingCharacters(in: .whitespaces).hasPrefix("π >") }) else {
            return .skip("无法确认 OMP 输入框")
        }
        let editor = lines.dropFirst(footer + 1).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        guard editor == ["╰─"] else { return .skip("OMP 输入框已有草稿或布局未知") }
        guard let transcript = observation.transcript else { return .skip("无法读取 OMP 当前会话记录") }
        // Ignore custom metadata, but never walk back past a newer user/tool/assistant message.
        for line in transcript.components(separatedBy: "\n").reversed() where !line.isEmpty {
            guard let data = line.data(using: .utf8),
                  let record = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                return .skip("OMP 会话记录尚未完整写入")
            }
            guard record["type"] as? String == "message" else { continue }
            guard let message = record["message"] as? [String: Any],
                  message["role"] as? String == "assistant", message["stopReason"] as? String == "error",
                  let error = message["errorMessage"] as? String, let reason = reason(for: error),
                  let id = record["id"] as? String else { return .skip("未发现当前容量或网络错误") }
            // Require the same error on screen; a transcript from another branch is insufficient.
            let normalized = clean(observation.text).components(separatedBy: .whitespacesAndNewlines).joined()
            let errorNormalized = error.components(separatedBy: .whitespacesAndNewlines).joined()
            guard !errorNormalized.isEmpty, normalized.contains(errorNormalized) else {
                return .skip("OMP 记录与当前错误画面不一致")
            }
            return .failure(FailureEvidence(reason: reason, message: error, fingerprint: digest(id + error)))
        }
        return .skip("OMP 会话中未发现当前错误")
    }

    private func isCodexFooter(_ line: String) -> Bool {
        let s = line.trimmingCharacters(in: .whitespaces)
        return s.isEmpty || s.hasPrefix("GPT-") || s.hasPrefix("gpt-") || s.contains("? for shortcuts")
            || s.hasPrefix("New activity ·") || s.hasPrefix("← for agents") || s.hasPrefix("↑ for history")
            || s.hasPrefix("context left") || s.hasPrefix("Tip:")
    }
    private func clean(_ value: String) -> String {
        value.replacingOccurrences(of: "\u{001B}\\[[0-?]*[ -/]*[@-~]", with: "", options: .regularExpression)
            .replacingOccurrences(of: "\r", with: "")
    }
    private func digest(_ value: String) -> String {
        SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined()
    }
}
