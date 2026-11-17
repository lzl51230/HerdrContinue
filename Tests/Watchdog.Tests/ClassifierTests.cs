using System.Text.Json;
using Watchdog.Core;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class ClassifierTests
{
    private readonly FailureClassifier classifier = new();
    private void Skips(Observation o) => Assert.Null(classifier.Assess(o).Evidence);
    [Fact] public void RealCapacityMessage() => Assert.Equal(FailureReason.Capacity, classifier.Assess(Observe()).Evidence!.Reason);
    [Fact] public void NetworkMessages()
    {
        foreach (var error in new[] { "stream disconnected before completion: error sending request", "Request timed out", "Connection reset by peer", "fetch failed", "网络连接失败", "unexpected status 502 Bad Gateway", "504 Gateway Timeout", "Connection timed out" })
            Assert.Equal(FailureReason.Network, classifier.Assess(Observe(Screen(error))).Evidence!.Reason);
    }
    [Theory]
    [InlineData(BadRequest)]
    [InlineData("{ \"detail\" : \"Bad Request\" }")]
    [InlineData("Bad Request")]
    [InlineData("unexpected status 400 Bad Request")]
    [InlineData("bAd ReQuEsT")]
    [InlineData("\u001b[31mBad Request\u001b[0m")]
    [InlineData("{\n  \"detail\": \"Bad Request\"\n  }")]
    [InlineData("unexpected status 400\n  Bad Request")]
    public void BadRequestMessages(string error) =>
        Assert.Equal(FailureReason.Network, classifier.Assess(Observe(Screen(error))).Evidence!.Reason);

    [Theory]
    [InlineData("400")]
    [InlineData("HTTP 400")]
    [InlineData("{\"detail\":\"Invalid request\"}")]
    public void OtherRequestErrorsAreNotIncluded(string error) => Skips(Observe(Screen(error)));

    [Fact] public void ExcludedFailures()
    {
        foreach (var error in new[] { "Conversation interrupted - use /feedback if something went wrong", "Context window exceeded", "Usage limit reached", "insufficient_quota: network error", "rate limit exceeded", "Authentication failed: connection error", "Request canceled", "certificate verify failed: network error" })
        {
            Skips(Observe(Screen(error)));
            Skips(Observe(Screen("Bad Request: " + error)));
        }
    }
    [Fact] public void NonReadyStatesNeverPrompt() { foreach (var state in new[] { "working", "blocked", "unknown" }) Skips(Observe(status: state)); }
    [Theory] [InlineData(Capacity)] [InlineData(BadRequest)]
    public void NormalCompletionAndHistoricalError(string error) => Skips(Observe($"■ {error}\n› 继续\n• Completed successfully.\n› Ask Codex to do anything\n  GPT-6 high\n"));
    [Theory] [InlineData(Capacity)] [InlineData(BadRequest)]
    public void LatestErrorWins(string error) => Skips(Observe($"■ {error}\n› 继续\n■ Conversation interrupted\n› Ask Codex to do anything\n"));
    [Theory] [InlineData(Capacity)] [InlineData(BadRequest)]
    public void ToolOutputAndQuotedErrorAreIgnored(string error)
    { foreach (var prefix in new[] { "  ■ ", "│ ■ ", "> ■ ", "  └ ■ " }) Skips(Observe(prefix + error + "\n› Ask Codex to do anything\n")); }
    [Theory] [InlineData(Capacity)] [InlineData(BadRequest)]
    public void DraftIsNotOverwritten(string error) => Skips(Observe(Screen(error, prompt: "My unfinished request")));
    [Fact] public void MultilineDraftIsNotOverwritten() => Skips(Observe($"■ {Capacity}\n› \n  second draft line\n  GPT-6 high\n"));
    [Fact] public void MissingPromptAndUnknownAgentAreSkipped()
    { Skips(Observe("■ " + Capacity)); Skips(Observe() with { Agent = new() { Kind = "unknown" } }); Skips(Observe("› ")); }
    [Fact] public void MissingIdentityIsSkipped() => Skips(Observe(pid: null));
    [Theory] [InlineData("Connection reset by peer")] [InlineData(BadRequest)] [InlineData("400 Bad Request")]
    public void OmpRequiresCurrentStructuredErrorAndEmptyEditor(string error)
    {
        var transcript = JsonSerializer.Serialize(new { type = "message", id = "error1", message = new { role = "assistant", stopReason = "error", errorMessage = error } });
        var screen = $"Error: {error}\n π > model\n╰─\n";
        var o = new Observation(TestData.Session, new() { Kind = "omp" }, screen, Transcript: transcript);
        Assert.Equal(FailureReason.Network, classifier.Assess(o).Evidence!.Reason);
        Skips(o with { Text = screen + "unfinished input" }); Skips(o with { Text = "π > model\n╰─" });
        Skips(o with { Text = "Error: fetch failed\n π > model\n╰─\n" });
        Skips(o with { Transcript = transcript + "\n{\"type\":\"message\",\"message\":{\"role\":\"user\"}}" });
        Skips(o with { Transcript = transcript + "\n{" });
    }
    [Fact] public void WrappedNativeErrorAndAnsi() => Assert.NotNull(classifier.Assess(Observe("\u001b[31m■ stream disconnected before completion:\u001b[0m\n  error sending request\n\n› Ask Codex to do anything\n")).Evidence);
    [Fact] public void CrLfHasSameEvidenceFingerprint() => Assert.Equal(classifier.Assess(Observe()).Evidence, classifier.Assess(Observe(Screen().Replace("\n", "\r\n"))).Evidence);
    [Fact] public void InputReadinessAndReadErrorsAreConservative()
    { Skips(Observe() with { Agent = new() { InteractiveReady = false } }); Skips(Observe() with { Agent = new() { LaunchPending = true } }); Skips(Observe() with { Error = "offline" }); }
}
