namespace Watchdog.Core;

public sealed record ResumeIntent(Observation Observation, FailureEvidence Evidence);

// All calls are serialized by MonitorEngine. This state machine has no I/O or wall clock.
public sealed class RecoveryMachine
{
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
    }
    private readonly Dictionary<string, Record> records = [];
    private readonly FailureClassifier classifier = new();
    public const double Cooldown = 10;

    public (AgentRow Row, ResumeIntent? Intent) Evaluate(Observation observation, double now)
    {
        if (!records.TryGetValue(observation.Key, out var record) || observation.ForegroundPid is not null && record.Occupant != observation.Occupant)
            record = new(observation.Occupant);
        var assessment = classifier.Assess(observation);
        var detail = assessment.Detail;
        ResumeIntent? intent = null;
        if (assessment.Evidence is not { } evidence)
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
                if (record.Pending != evidence) { record.Pending = evidence; record.Deadline = Math.Max(now + Cooldown, record.LastAttempt + Cooldown); }
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
        if (fresh.Key != intent.Observation.Key || fresh.Occupant != intent.Observation.Occupant ||
            fresh.Agent.PaneId != intent.Observation.Agent.PaneId || classifier.Assess(fresh).Evidence != intent.Evidence ||
            !records.TryGetValue(fresh.Key, out var record) || record.Pending != intent.Evidence || record.Deadline is not { } deadline || now < deadline)
            return false;
        record.Pending = null; record.Deadline = null; record.Consumed = intent.Evidence.Fingerprint;
        record.ConsumedSequence = fresh.Agent.StateChangeSeq ?? 0; record.SawWorking = false;
        record.LastAttempt = now; record.Attempts++; record.Outcome = "已提交，等待 agent 响应";
        return true;
    }

    public void SubmissionFailed(string key, bool definitelyNotSent, double now)
    {
        if (!records.TryGetValue(key, out var record)) return;
        record.Outcome = definitelyNotSent ? "未发送，等待连接恢复" : "发送结果不明确，等待新证据以避免重复";
        if (definitelyNotSent) { record.Consumed = null; record.Attempts = Math.Max(0, record.Attempts - 1); record.LastAttempt = now; }
    }
    public void SubmissionConfirmed(string key) { if (records.TryGetValue(key, out var r)) r.SawWorking = true; }
    public void Suspend(IEnumerable<string> keys)
    {
        foreach (var key in keys) if (records.TryGetValue(key, out var r)) { r.Pending = null; r.Deadline = null; }
    }
    public void Stop() => Suspend(records.Keys);
}
