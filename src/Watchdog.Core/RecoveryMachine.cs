namespace Watchdog.Core;

public sealed record ResumeIntent(Observation Observation, FailureEvidence Evidence, bool AfterInterrupt = false)
{
    public bool SendsEscape => Evidence.ReconnectContext is not null && !AfterInterrupt;
    public bool SameTarget(Observation other) => other.Key == Observation.Key && other.Occupant == Observation.Occupant &&
        other.Agent.PaneId == Observation.Agent.PaneId;
}

// All calls are serialized by MonitorEngine. This state machine has no I/O or wall clock.
public sealed class RecoveryMachine
{
    private const double InterruptConfirmationSeconds = 15;

    private sealed class Record(string occupant)
    {
        public string Occupant = occupant;
        public FailureEvidence? Pending;
        public double? Deadline;
        public string? Consumed;
        public ulong ConsumedSequence;
        public bool SawWorking;
        public int Attempts;
        public double LastAttempt = double.NegativeInfinity;
        public string? Outcome;
        public ResumeIntent? Interrupted;
        public bool EscapeConfirmed;
        public bool RetryPrompt;
    }
    private readonly Dictionary<string, Record> records = [];
    private readonly FailureClassifier classifier = new();
    public int CooldownSeconds { get; private set; } = 5;
    public void SetCooldownSeconds(int seconds)
    {
        InterventionCooldown.Validate(seconds); CooldownSeconds = seconds;
        foreach (var record in records.Values)
            if (record.Pending is not null && (record.Interrupted is null || record.RetryPrompt)) record.Deadline = record.LastAttempt + seconds;
    }

    public (AgentRow Row, ResumeIntent? Intent) Evaluate(Observation observation, double now)
    {
        if (!records.TryGetValue(observation.Key, out var record) || observation.ForegroundPid is not null && record.Occupant != observation.Occupant)
            record = new(observation.Occupant);
        var assessment = classifier.Assess(observation);
        var detail = assessment.Detail;
        ResumeIntent? intent = null;
        if (record.Interrupted is { } interrupted)
        {
            record.Pending = null;
            record.Deadline = null;
            var agentReady = observation.Agent.AcceptsInput;
            var stillWorking = observation.Agent.CanInspectReconnect &&
                observation.Agent.StateChangeSeq == interrupted.Observation.Agent.StateChangeSeq;
            if (!interrupted.SameTarget(observation) || !agentReady && !stillWorking ||
                !record.RetryPrompt && now > record.LastAttempt + InterruptConfirmationSeconds)
            {
                record.Interrupted = null;
            }
            else if (FailureClassifier.HasInterruptionScreen(observation, interrupted.Evidence.ReconnectContext!))
            {
                detail = "已中断容量重连，等待续接";
                if (agentReady && record.EscapeConfirmed)
                {
                    record.Pending = interrupted.Evidence;
                    record.Deadline = record.RetryPrompt ? record.LastAttempt + CooldownSeconds : now;
                    if (now >= record.Deadline) intent = new(observation, interrupted.Evidence, AfterInterrupt: true);
                }
            }
            else if (stillWorking && assessment.Evidence == interrupted.Evidence)
                detail = "已请求中断容量重连，等候输入框就绪";
            else
                record.Interrupted = null;
        }
        else if (assessment.Evidence is not { } evidence)
        {
            record.Pending = null; record.Deadline = null;
            if (observation.Agent.Status == "working") { record.SawWorking = true; record.Outcome = null; }
        }
        else
        {
            var sequence = observation.Agent.StateChangeSeq ?? 0;
            var newRound = record.SawWorking && sequence > record.ConsumedSequence;
            var newEvidence = evidence.Fingerprint != record.Consumed;
            if (record.Consumed is not null && !newRound && !(newEvidence && sequence > record.ConsumedSequence))
            {
                record.Pending = null; record.Deadline = null;
                detail = record.Outcome ?? "已提交，等待新的运行或失败证据";
            }
            else
            {
                record.Pending = evidence; record.Deadline = double.IsNegativeInfinity(record.LastAttempt) ? now : record.LastAttempt + CooldownSeconds;
                if (now >= record.Deadline) { intent = new(observation, evidence); detail = "冷却结束，复核后发送"; }
            }
        }
        records[observation.Key] = record;
        var agent = observation.Agent;
        return (new(observation.Key, agent.Name ?? agent.TerminalTitleStripped ?? agent.PaneId,
            $"{observation.Session.Name} / {agent.WorkspaceId} / {agent.PaneId}", agent.Kind ?? "unknown",
            agent.Status, detail, record.Deadline, record.Attempts), intent);
    }

    public bool Claim(ResumeIntent intent, Observation fresh, double now)
    {
        if (!records.TryGetValue(intent.Observation.Key, out var record) || record.Pending != intent.Evidence || record.Deadline is not { } deadline || now < deadline)
            return false;
        if (intent.AfterInterrupt)
        {
            if (!intent.SameTarget(fresh) || !fresh.Agent.AcceptsInput || record.Interrupted?.Evidence != intent.Evidence ||
                !record.EscapeConfirmed || !record.RetryPrompt && now > record.LastAttempt + InterruptConfirmationSeconds ||
                fresh.Agent.StateChangeSeq != intent.Observation.Agent.StateChangeSeq ||
                !FailureClassifier.HasInterruptionScreen(fresh, intent.Evidence.ReconnectContext!))
            {
                record.Interrupted = null;
                return false;
            }
        }
        else if (!intent.SameTarget(fresh) || classifier.Assess(fresh).Evidence != intent.Evidence || now < record.LastAttempt + CooldownSeconds ||
            intent.SendsEscape && fresh.Agent.StateChangeSeq != intent.Observation.Agent.StateChangeSeq)
            return false;
        record.Pending = null; record.Deadline = null; record.Consumed = intent.Evidence.Fingerprint;
        record.ConsumedSequence = fresh.Agent.StateChangeSeq ?? 0; record.SawWorking = false;
        record.LastAttempt = now;
        if (intent.SendsEscape)
        {
            record.Outcome = "已请求中断本轮容量重连";
            record.Interrupted = intent;
            record.EscapeConfirmed = false;
            record.RetryPrompt = false;
        }
        else
        {
            record.Attempts++;
            record.EscapeConfirmed = false;
            record.Outcome = "已提交，等待 agent 响应";
        }
        return true;
    }

    public void EscapeFinished(ResumeIntent intent, bool confirmed, bool definitelyNotSent)
    {
        if (!records.TryGetValue(intent.Observation.Key, out var record) || record.Interrupted != intent) return;
        if (confirmed) record.EscapeConfirmed = true;
        else
        {
            record.Interrupted = null;
            if (definitelyNotSent) record.Consumed = null;
            record.Outcome = definitelyNotSent ? "Esc 未发送，等待重试" : "Esc 结果不明确，等待新的重连证据";
        }
    }

    public void SubmissionFailed(string key, bool definitelyNotSent, double now)
    {
        if (!records.TryGetValue(key, out var record)) return;
        record.Outcome = definitelyNotSent ? "未发送，等待连接恢复" : "发送结果不明确，等待新证据以避免重复";
        if (definitelyNotSent && record.Interrupted is not null)
        {
            record.EscapeConfirmed = true;
            record.RetryPrompt = true;
        }
        else record.Interrupted = null;
        if (definitelyNotSent)
        {
            if (record.Interrupted is null) record.Consumed = null;
            record.Attempts = Math.Max(0, record.Attempts - 1);
        }
    }
    public void SubmissionConfirmed(string key) { if (records.TryGetValue(key, out var r)) { r.SawWorking = true; r.Interrupted = null; } }
    public void Suspend(IEnumerable<string> keys)
    {
        foreach (var key in keys) if (records.TryGetValue(key, out var r)) { r.Pending = null; r.Deadline = null; r.Interrupted = null; }
    }
    public double? Deadline(string key) => records.TryGetValue(key, out var r) ? r.Deadline : null;
    public void Stop() => Suspend(records.Keys);
}
