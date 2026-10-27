using Watchdog.Core;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class RecoveryTests
{
    [Fact] public void TenSecondBoundaryAndNoDuplicateClaim()
    {
        var m = new RecoveryMachine(); Assert.Null(m.Evaluate(Observe(), 100).Intent); Assert.Null(m.Evaluate(Observe(), 109.999).Intent);
        var intent = m.Evaluate(Observe(), 110).Intent!; Assert.NotNull(intent); Assert.True(m.Claim(intent, Observe(), 110)); Assert.False(m.Claim(intent, Observe(), 110));
        Assert.Null(m.Evaluate(Observe(), 500).Intent); Assert.Null(m.Evaluate(Observe(), 510).Intent);
    }
    [Fact] public void IndependentCooldowns()
    {
        var m = new RecoveryMachine(); var a = Observe(terminal: "a"); var b = Observe(terminal: "b");
        m.Evaluate(a, 0); m.Evaluate(b, 3); Assert.NotNull(m.Evaluate(a, 10).Intent); Assert.Null(m.Evaluate(b, 10).Intent); Assert.NotNull(m.Evaluate(b, 13).Intent);
    }
    [Fact] public void RecoveryDuringCooldownCancels()
    { var m = new RecoveryMachine(); m.Evaluate(Observe(), 0); m.Evaluate(Observe(status: "working", sequence: 2), 5); Assert.Null(m.Evaluate(Observe(sequence: 3), 10).Intent); Assert.NotNull(m.Evaluate(Observe(sequence: 3), 20).Intent); }
    [Fact] public void ClaimRejectsDraftBlockedReplacementAndPaneMove()
    {
        var m = new RecoveryMachine(); m.Evaluate(Observe(), 0); var intent = m.Evaluate(Observe(), 10).Intent!;
        foreach (var fresh in new[] { Observe(Screen(prompt: "draft")), Observe(status: "blocked"), Observe(pid: 101), Observe(terminal: "replacement"), Observe() with { Agent = new() { PaneId = "w2:p9" } } }) Assert.False(m.Claim(intent, fresh, 10));
    }
    [Fact] public void RepeatedSameFailureAfterWorkingRetriesWithoutLimit()
    {
        var m = new RecoveryMachine();
        for (var round = 0; round < 40; round++)
        {
            var sequence = (ulong)(round * 2 + 1); var start = round * 20; var failure = Observe(sequence: sequence);
            Assert.Null(m.Evaluate(failure, start).Intent); var intent = m.Evaluate(failure, start + 10).Intent!; Assert.NotNull(intent); Assert.True(m.Claim(intent, failure, start + 10));
            m.Evaluate(Observe(status: "working", sequence: sequence + 1), start + 11);
        }
    }
    [Fact] public void FastFailureBetweenPollsRearms()
    { var m = new RecoveryMachine(); m.Evaluate(Observe(), 0); Assert.True(m.Claim(m.Evaluate(Observe(), 10).Intent!, Observe(), 10)); m.SubmissionConfirmed(Observe().Key); Assert.Null(m.Evaluate(Observe(sequence: 3), 12).Intent); Assert.NotNull(m.Evaluate(Observe(sequence: 3), 22).Intent); }
    [Fact] public void SeenStateChangeAloneDoesNotResend()
    { var m = new RecoveryMachine(); m.Evaluate(Observe(), 0); m.Claim(m.Evaluate(Observe(), 10).Intent!, Observe(), 10); Assert.Null(m.Evaluate(Observe(status: "done", sequence: 200), 20).Intent); Assert.Null(m.Evaluate(Observe(status: "done", sequence: 200), 40).Intent); }
    [Fact] public void StopClearsCountdownButPreservesAmbiguousSubmission()
    { var m = new RecoveryMachine(); m.Evaluate(Observe(), 0); m.Stop(); Assert.Null(m.Evaluate(Observe(), 20).Intent); Assert.True(m.Claim(m.Evaluate(Observe(), 30).Intent!, Observe(), 30)); m.SubmissionFailed(Observe().Key, false, 30); m.Stop(); Assert.Null(m.Evaluate(Observe(), 50).Intent); Assert.Null(m.Evaluate(Observe(), 100).Intent); }
    [Fact] public void DefinitelyUnsentRequestRetriesAfterCooldown()
    { var m = new RecoveryMachine(); m.Evaluate(Observe(), 0); m.Claim(m.Evaluate(Observe(), 10).Intent!, Observe(), 10); m.SubmissionFailed(Observe().Key, true, 10); Assert.Null(m.Evaluate(Observe(), 11).Intent); Assert.Null(m.Evaluate(Observe(), 20).Intent); Assert.NotNull(m.Evaluate(Observe(), 21).Intent); }
    [Fact] public void ReconnectRestartsCooldown()
    { var m = new RecoveryMachine(); m.Evaluate(Observe(), 0); m.Suspend([Observe().Key]); Assert.Null(m.Evaluate(Observe(), 30).Intent); Assert.NotNull(m.Evaluate(Observe(), 40).Intent); }
}
