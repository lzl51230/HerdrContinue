using Watchdog.Core;
using Xunit;
using static Watchdog.Tests.TestData;
namespace Watchdog.Tests;
public sealed class RecoveryTests
{
    private static void Send(RecoveryMachine m, Observation o, double now)
    { var intent = m.Evaluate(o, now).Intent; Assert.NotNull(intent); Assert.True(m.Claim(intent, o, now)); m.SubmissionConfirmed(o.Key); }
    [Theory] [InlineData(Capacity)] [InlineData(BadRequest)]
    public void FirstFailureIsImmediateAndOldEvidenceNeverResends(string error)
    { var m = new RecoveryMachine(); var o = Observe(Screen(error)); Send(m, o, 100); Assert.Null(m.Evaluate(o, 1000).Intent); }
    [Theory] [InlineData(5, Capacity)] [InlineData(10, Capacity)] [InlineData(5, BadRequest)] [InlineData(10, BadRequest)]
    public void SubsequentFailureWaitsOnlyRemainingTime(int seconds, string error)
    {
        var m = new RecoveryMachine(); m.SetCooldownSeconds(seconds); Send(m, Observe(Screen(error)), 100);
        var next = Observe(Screen(error), sequence: 3); Assert.Equal(100 + seconds, m.Evaluate(next, 102).Row.Deadline);
        Assert.Null(m.Evaluate(next, 100 + seconds - .001).Intent); Send(m, next, 100 + seconds);
        Send(m, Observe(Screen(error), sequence: 5), 200);
    }
    [Fact] public void AgentsAndNewOccupantsAreIndependent()
    { var m = new RecoveryMachine(); Send(m, Observe(terminal: "a"), 0); Send(m, Observe(terminal: "b"), 1); Send(m, Observe(terminal: "a", pid: 101), 2); }
    [Fact] public void EvidenceChangesAndReconnectDoNotRestartCooldown()
    {
        var m = new RecoveryMachine(); Send(m, Observe(), 0); m.Evaluate(Observe(sequence: 3), 1); m.Stop();
        var next = Observe(Screen("network error"), sequence: 4); Assert.Equal(5, m.Evaluate(next, 4).Row.Deadline); Send(m, next, 5);
    }
    [Fact] public void ClaimRejectsDraftBlockedReplacementAndPaneMove()
    {
        var m = new RecoveryMachine(); var intent = m.Evaluate(Observe(), 0).Intent!;
        foreach (var fresh in new[] { Observe(Screen(prompt: "draft")), Observe(status: "blocked"), Observe(pid: 101), Observe(terminal: "replacement"), Observe() with { Agent = new() { PaneId = "w2:p9" } } }) Assert.False(m.Claim(intent, fresh, 0));
        Assert.True(m.Claim(intent, Observe(), 0)); Assert.False(m.Claim(intent, Observe(), 0));
    }
    [Fact] public void FailedSubmissionUsesAttemptStartAndAmbiguityRemainsConsumed()
    {
        var m = new RecoveryMachine(); Send(m, Observe(), 0); m.SubmissionFailed(Observe().Key, true, 3);
        Assert.Null(m.Evaluate(Observe(), 4.999).Intent); Send(m, Observe(), 5);
        m.SubmissionFailed(Observe().Key, false, 6); m.Stop(); Assert.Null(m.Evaluate(Observe(), 100).Intent);
    }
    [Fact] public void LiveChangesRecalculateAndRecheckAnAlreadyIssuedIntent()
    {
        var m = new RecoveryMachine(); Send(m, Observe(), 0); var next = Observe(sequence: 3);
        var intent = m.Evaluate(next, 5).Intent!; m.SetCooldownSeconds(10); Assert.False(m.Claim(intent, next, 5));
        Assert.Equal(10, m.Evaluate(next, 6).Row.Deadline); m.SetCooldownSeconds(1); Send(m, next, 6);
    }
    [Fact] public void StateChangeWithoutWorkingDoesNotRearm()
    { var m = new RecoveryMachine(); Assert.True(m.Claim(m.Evaluate(Observe(), 0).Intent!, Observe(), 0)); m.SubmissionFailed(Observe().Key, false, 1); Assert.Null(m.Evaluate(Observe(status: "done", sequence: 200), 100).Intent); }
}
