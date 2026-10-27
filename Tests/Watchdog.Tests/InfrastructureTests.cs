using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;

namespace Watchdog.Tests;

public sealed class InfrastructureTests
{
    [Fact] public async Task ProbeAlwaysUsesExplicitProxyEvenWithNoProxyEnvironment()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var old = Environment.GetEnvironmentVariable("NO_PROXY"); Environment.SetEnvironmentVariable("NO_PROXY", "*");
        try
        {
            var request = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(); using var stream = client.GetStream();
                var line = await LocalFixture.ReadLineAsync(stream, default);
                await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()); return line;
            });
            var service = new LiveClashService(proxyAddress: new Uri($"http://127.0.0.1:{port}"), probeUrls: ["http://watchdog.invalid/probe"]);
            Assert.True((await service.ProbeProxyAsync(default)).Healthy);
            Assert.StartsWith("GET http://watchdog.invalid/probe HTTP/1.1", await request.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { Environment.SetEnvironmentVariable("NO_PROXY", old); }
    }
    [Fact] public async Task BrokenProxyNeverFallsBackToReachableLocalTarget()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        using var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start(); var port = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        var service = new LiveClashService(proxyAddress: new Uri($"http://127.0.0.1:{port}"), probeUrls: [$"http://127.0.0.1:{((IPEndPoint)target.LocalEndpoint).Port}/probe"]);
        Assert.False((await service.ProbeProxyAsync(default)).Healthy); Assert.False(target.Pending());
    }
    [Fact] public void TranscriptReadsUnicodeAndCrLfAndRejectsSiblingDirectory()
    {
        var root = TestData.TempDirectory(); var sibling = root + "-other"; Directory.CreateDirectory(sibling);
        try
        {
            var path = Path.Combine(root, "中文会话.jsonl"); File.WriteAllText(path, "{\"message\":\"连接中断\"}\r\n", new UTF8Encoding(false));
            var reader = new TranscriptReader(root); Assert.Contains("连接中断", reader.Read(path));
            var outside = Path.Combine(sibling, "other.jsonl"); File.WriteAllText(outside, "private"); Assert.Null(reader.Read(outside)); Assert.Null(reader.Read("relative.jsonl"));
        }
        finally { Directory.Delete(root, true); Directory.Delete(sibling, true); }
    }
    [Fact] public void TranscriptTailDropsPartialRecordAndNeverExceedsBound()
    {
        var root = TestData.TempDirectory();
        try
        {
            var path = Path.Combine(root, "large.jsonl"); File.WriteAllText(path, new string('x', 300000) + "\n{\"id\":\"last\"}\n");
            Assert.Equal("{\"id\":\"last\"}\n", new TranscriptReader(root).Read(path));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void TranscriptRejectsSymlinkEscape()
    {
        var root = TestData.TempDirectory(); var outside = root + ".jsonl"; File.WriteAllText(outside, "private");
        try
        {
            var link = Path.Combine(root, "escape.jsonl");
            try { File.CreateSymbolicLink(link, outside); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
            Assert.Null(new TranscriptReader(root).Read(link));
        }
        finally { Directory.Delete(root, true); File.Delete(outside); }
    }
    [Fact] public void TranscriptHandlesPlatformCaseSensitivity()
    {
        var root = TestData.TempDirectory();
        try
        {
            var path = Path.Combine(root, "session.JSONL"); File.WriteAllText(path, "record\n");
            if (OperatingSystem.IsWindows()) Assert.Equal("record\n", new TranscriptReader(root).Read(path)); else Assert.Null(new TranscriptReader(root).Read(path));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public void MissingAgentIdentityAndAgentKindCannotBecomeDefaultCodex()
    {
        using var invalid = JsonDocument.Parse("{\"agent_status\":\"idle\"}"); Assert.Throws<HerdrException>(() => LiveHerdrService.ParseAgent(invalid.RootElement));
        using var valid = JsonDocument.Parse("{\"agent_status\":\"idle\",\"pane_id\":\"w1:p1\",\"terminal_id\":\"t\",\"workspace_id\":\"w1\",\"tab_id\":\"w1:t1\"}");
        var agent = LiveHerdrService.ParseAgent(valid.RootElement); Assert.Null(agent.Kind); Assert.Null(agent.StateChangeSeq);
        Assert.Null(new FailureClassifier().Assess(TestData.Observe() with { Agent = agent }).Evidence);
    }
    [Fact] public void LogRotationRetainsOnlyPreviousAndWritesValidJson()
    {
        var root = TestData.TempDirectory(); var path = Path.Combine(root, "events.jsonl");
        try
        {
            File.WriteAllText(path, new string('x', 2_000_001)); var log = new JsonEventLog(path); log.Append(new(DateTimeOffset.UnixEpoch, "测试", "连接恢复"));
            Assert.True(File.Exists(path + ".previous")); Assert.True(new FileInfo(path).Length < 1000); Assert.Null(log.LastError);
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); Assert.Equal("连接恢复", doc.RootElement.GetProperty("message").GetString());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact] public async Task ProcessKeepsStdoutJsonSeparateFromStderrAndQuotesArguments()
    {
        var runner = new ProcessRunner();
        var result = OperatingSystem.IsWindows()
            ? await runner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('{\"ok\":true}'); [Console]::Error.Write('warning')"], TimeSpan.FromSeconds(10))
            : await runner.RunAsync("/bin/sh", ["-c", "printf '%s' \"$1\"; printf warning >&2", "qa", "{\"ok\":true}"], TimeSpan.FromSeconds(3));
        Assert.Equal(0, result.ExitCode); Assert.Equal("warning", result.Error); using var json = JsonDocument.Parse(result.Output); Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
    }
    [Fact] public async Task ProcessTimeoutKillsChildAndDrainsPipes()
    {
        var runner = new ProcessRunner();
        var task = OperatingSystem.IsWindows()
            ? runner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], TimeSpan.FromMilliseconds(300))
            : runner.RunAsync("/bin/sh", ["-c", "sleep 30"], TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<HerdrException>(() => task.WaitAsync(TimeSpan.FromSeconds(4)));
    }
}
