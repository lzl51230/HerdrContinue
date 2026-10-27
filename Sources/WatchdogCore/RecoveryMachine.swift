import Foundation

public struct ResumeIntent: Sendable {
    public let observation: Observation
    public let evidence: FailureEvidence
}

/// Pure per-agent state machine. Its clock is injected so cooldown tests never sleep.
public struct RecoveryMachine {
    private struct Record {
        var occupant: String
        var pending: FailureEvidence?
        var deadline: TimeInterval?
        var consumed: String?
        var consumedSequence: UInt64 = 0
        var sawWorking = false
        var attempts = 0
        var lastAttempt: TimeInterval = -.infinity
        var outcome: String?
    }
    private var records: [String: Record] = [:]
    private let classifier = FailureClassifier()
    public static let cooldown: TimeInterval = 10
    public init() {}

    public mutating func evaluate(_ observation: Observation, now: TimeInterval) -> (AgentRow, ResumeIntent?) {
        var record = records[observation.key] ?? Record(occupant: observation.occupant)
        // Working snapshots omit process inspection. Do not mistake missing PID for a replacement.
        if observation.foregroundPID != nil && record.occupant != observation.occupant {
            record = Record(occupant: observation.occupant)
        }
        var detail: String
        var intent: ResumeIntent?
        switch classifier.assess(observation) {
        case .skip(let message):
            detail = message
            record.pending = nil; record.deadline = nil
            if observation.agent.agent_status == "working" { record.sawWorking = true; record.outcome = nil }
        case .failure(let evidence):
            let sequence = observation.agent.state_change_seq ?? 0
            let newRound = record.sawWorking && sequence > record.consumedSequence
            let newEvidence = evidence.fingerprint != record.consumed
            if record.consumed != nil && !newRound && !(newEvidence && sequence > record.consumedSequence) {
                record.pending = nil; record.deadline = nil
                detail = record.outcome ?? "已提交，等待新的运行或失败证据"
            } else {
                if record.pending != evidence {
                    record.pending = evidence
                    record.deadline = max(now + Self.cooldown, record.lastAttempt + Self.cooldown)
                }
                detail = evidence.reason.rawValue
                if let deadline = record.deadline, now >= deadline {
                    intent = ResumeIntent(observation: observation, evidence: evidence)
                    detail = "冷却结束，复核后发送"
                }
            }
        }
        records[observation.key] = record
        return (AgentRow(id: observation.key,
                         title: observation.agent.name ?? observation.agent.terminal_title_stripped ?? observation.agent.pane_id,
                         location: "\(observation.session.name) / \(observation.agent.workspace_id) / \(observation.agent.pane_id)",
                         kind: observation.agent.agent ?? "unknown", state: observation.agent.agent_status,
                         detail: detail, deadline: record.deadline, attempts: record.attempts), intent)
    }

    /// Revalidation is mandatory immediately before claiming a submission.
    public mutating func claim(_ intent: ResumeIntent, fresh: Observation, now: TimeInterval) -> Bool {
        guard fresh.key == intent.observation.key, fresh.occupant == intent.observation.occupant,
              fresh.agent.pane_id == intent.observation.agent.pane_id,
              classifier.assess(fresh) == .failure(intent.evidence),
              let record = records[fresh.key], record.pending == intent.evidence,
              let deadline = record.deadline, now >= deadline else { return false }
        var next = record
        next.pending = nil; next.deadline = nil; next.consumed = intent.evidence.fingerprint
        next.consumedSequence = fresh.agent.state_change_seq ?? 0
        next.sawWorking = false; next.lastAttempt = now; next.attempts += 1
        next.outcome = "已提交，等待 agent 响应"
        records[fresh.key] = next
        return true
    }

    public mutating func submissionFailed(key: String, definitelyNotSent: Bool, now: TimeInterval) {
        guard var record = records[key] else { return }
        record.outcome = definitelyNotSent ? "未发送，等待连接恢复" : "发送结果不明确，等待新证据以避免重复"
        if definitelyNotSent {
            record.consumed = nil; record.attempts = max(0, record.attempts - 1)
            record.lastAttempt = now
        }
        records[key] = record
    }

    public mutating func submissionConfirmed(key: String) {
        // agent.prompt's server-side wait confirmed actual working activity, not just a write.
        records[key]?.sawWorking = true
    }

    public mutating func suspend(keys: Set<String>) {
        for key in keys { records[key]?.pending = nil; records[key]?.deadline = nil }
    }
    public mutating func retain(keys: Set<String>) { records = records.filter { keys.contains($0.key) } }
    public mutating func stop() {
        // Preserve consumed errors across stop/start to avoid resubmitting an unresolved write.
        for key in records.keys { records[key]?.pending = nil; records[key]?.deadline = nil }
    }
}
