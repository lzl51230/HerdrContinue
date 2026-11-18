using Watchdog.Core;
using Watchdog.Infrastructure;
using System.Text;
using System.Text.Json;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class ReconnectTests
{
    private const string History = "› Build a feature\n• Ran checks\n  └ output\n    + 44 lines (⌃t to expand)\n\n";
    private const string Header = "• Reconnecting... 1/5 (1h 09m 26s • esc to interrupt)";
    private const string Tip = "  └ Tip: Use /feedback to send logs to the maintainers when something looks off.";
    private const string Block = Header + "\n  └ " + Capacity + "\n" + Tip;
    private const string Editor = "\n\n› Ask Codex to do anything\n\n  GPT-6.2-Astra high · ~/project\n  ← for agents · ? for shortcuts   ⚠ 1 warning · f2 to view\n";
    private const string Interrupted = "■ Conversation interrupted - use /feedback if something went wrong";
    private static Observation Working(string block = Block) => Observe(History + block + Editor, status: "working");
    private static Observation Stopped(string marker = Interrupted) => Observe(History + marker + Editor, sequence: 2);

    [Fact]
    public void MatchesCurrentNativeBlockIndependentlyOfTimerAndFeedbackTip()
    {
        var classifier = new FailureClassifier();
        var evidence = classifier.Assess(Working()).Evidence;
        Assert.NotNull(evidence);
        Assert.Equal(FailureReason.Capacity, evidence.Reason);
        Assert.NotNull(evidence.ReconnectContext);
        Assert.Equal(evidence, classifier.Assess(Working(Block.Replace("1h 09m 26s", "4s").Replace(Tip, ""))).Evidence);
        foreach (var block in new[] { Capacity, Block.Replace(Capacity, "Connection reset"), "  " + Block,
            Block + "\n• Completed successfully.", Block + "\n› new task", Block + "\n■ Conversation interrupted" })
            Assert.Null(classifier.Assess(Working(block)).Evidence);
        Assert.Null(classifier.Assess(Working() with { Text = History + Block }).Evidence);
        Assert.Null(classifier.Assess(Working() with { Text = History + Block + Editor.Replace("Ask Codex to do anything", "draft") }).Evidence);
        Assert.Null(classifier.Assess(Working() with { ForegroundPid = null }).Evidence);
    }

    [Fact]
    public async Task MonitorInterruptsOnceThenPromptsOnceAfterAcknowledgedInterruption()
    {
        var service = new FakeHerdr { Value = Working() };
        await using var monitor = new MonitorEngine(service, new FakeClash());
        await monitor.StartAsync(false);
        await monitor.Proxy.CheckNowAsync();
        await monitor.ScanNowAsync();
        await Eventually(() => monitor.Snapshot.Events.Any(e => e.Message.Contains("已确认 Esc")));
        service.Value = Working(Block.Replace("26s", "28s"));
        await monitor.ScanNowAsync();
        Assert.Equal(0, service.Sent);
        service.Value = Stopped();
        await monitor.ScanNowAsync();
        await Eventually(() => monitor.Snapshot.Events.Any(e => e.Message.Contains("已确认提交")));
        await monitor.ScanNowAsync();
        Assert.Equal(1, service.Interrupts);
        Assert.Equal(1, service.Sent);
        Assert.Equal(1, monitor.Snapshot.Rows.Single().Attempts);
    }

    [Fact]
    public void InvalidTransitionsDiscardThePendingContinuation()
    {
        foreach (var changed in new[] { Stopped("• Completed successfully."), Stopped("› New request\n" + Interrupted),
            Stopped() with { ForegroundPid = 101 }, Working("• Running tools"), Stopped() with { Text = History + Interrupted + Editor.Replace("Ask Codex to do anything", "draft") } })
        {
            var machine = new RecoveryMachine();
            var escape = machine.Evaluate(Working(), 0).Intent!;
            Assert.False(machine.Claim(escape, Working("• Running tools"), 0));
            Assert.True(machine.Claim(escape, Working(), 0));
            machine.EscapeFinished(escape, true, false);
            Assert.Null(machine.Evaluate(changed, 1).Intent);
            Assert.Null(machine.Evaluate(Stopped(), 2).Intent);
        }
    }

    [Fact]
    public void UncertainEscapeOrStopCannotAuthorizeLaterInput()
    {
        foreach (var confirmed in new[] { false, true })
        {
            var machine = new RecoveryMachine();
            var escape = machine.Evaluate(Working(), 0).Intent!;
            Assert.True(machine.Claim(escape, Working(), 0));
            machine.EscapeFinished(escape, confirmed, false);
            if (confirmed) machine.Stop();
            Assert.Null(machine.Evaluate(Working(), 60).Intent);
            Assert.Null(machine.Evaluate(Stopped(), 61).Intent);
        }
    }

    [Fact]
    public void StatusMayLagTheInterruptedScreenAndAnUnsentPromptRetriesAfterCooldown()
    {
        var machine = new RecoveryMachine();
        var escape = machine.Evaluate(Working(), 0).Intent!;
        Assert.True(machine.Claim(escape, Working(), 0));
        machine.EscapeFinished(escape, true, false);
        Assert.Null(machine.Evaluate(Stopped() with { Agent = Working().Agent }, 1).Intent);
        var prompt = machine.Evaluate(Stopped(), 2).Intent!;
        Assert.True(machine.Claim(prompt, Stopped(), 2));
        machine.SubmissionFailed(Stopped().Key, true, 2);
        Assert.Null(machine.Evaluate(Stopped(), 6).Intent);
        var retry = machine.Evaluate(Stopped(), 7).Intent!;
        Assert.True(retry.AfterInterrupt);
        Assert.True(machine.Claim(retry, Stopped(), 7));
    }

    [Fact]
    public void LateOrReplacedInterruptionCannotBeClaimed()
    {
        foreach (var late in new[] { true, false })
        {
            var machine = new RecoveryMachine();
            var escape = machine.Evaluate(Working(), 0).Intent!;
            Assert.True(machine.Claim(escape, Working(), 0));
            machine.EscapeFinished(escape, true, false);
            var prompt = machine.Evaluate(Stopped(), 1).Intent!;
            var fresh = late ? Stopped() : Stopped() with { ForegroundPid = 101 };
            Assert.False(machine.Claim(prompt, fresh, late ? 60 : 2));
            Assert.Null(machine.Evaluate(Stopped(), 61).Intent);
        }
    }

    [Theory, MemberData(nameof(TransportTests.Transports), MemberType = typeof(TransportTests))]
    public async Task EscapeUsesTargetedHerdrKeyRpc(EndpointKind kind)
    {
        JsonElement request = default;
        await using var fixture = new LocalFixture(kind, async (stream, ct) =>
        {
            using var doc = JsonDocument.Parse(await LocalFixture.ReadLineAsync(stream, ct));
            request = doc.RootElement.Clone();
            var response = JsonSerializer.Serialize(new { id = request.GetProperty("id"), result = new { } });
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response + "\n"), ct);
        });
        var observation = Working() with { Session = new("test", fixture.Endpoint) };
        await new LiveHerdrService().InterruptAsync(observation, default);
        Assert.Equal("agent.send_keys", request.GetProperty("method").GetString());
        var args = request.GetProperty("params");
        Assert.Equal(observation.Agent.PaneId, args.GetProperty("target").GetString());
        Assert.Equal("esc", args.GetProperty("keys").EnumerateArray().Single().GetString());
    }
}
