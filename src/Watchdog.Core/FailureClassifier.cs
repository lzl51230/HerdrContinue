using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watchdog.Core;

public sealed class FailureClassifier
{
    private static readonly string[] Excluded = ["context window", "context length", "context_length", "maximum context",
        "insufficient_quota", "quota exceeded", "usage limit", "credit balance", "billing", "payment required",
        "rate limit", "rate_limit", "too many requests", "unauthorized", "authentication", "invalid api key", "permission denied",
        "conversation interrupted", "user interrupted", "cancelled", "canceled", "certificate verify failed",
        "上下文", "额度", "余额", "认证", "手动中断"];
    private static readonly string[] Capacity = ["at capacity", "model capacity", "overloaded", "over capacity", "server is busy",
        "service unavailable", "模型容量不足", "模型已满", "服务过载", "模型繁忙"];
    private static readonly string[] Network = ["stream disconnected", "stream closed", "stream terminated", "connection reset",
        "connection refused", "connection closed", "connection error", "connection failed", "error connecting", "network error",
        "network connection", "network is unreachable", "fetch failed", "failed to fetch", "error sending request", "request timed out",
        "request timeout", "connect timeout", "connection timed out", "read timeout", "operation timed out", "dns", "econnreset",
        "econnrefused", "etimedout", "enotfound", "socket hang up", "tls handshake", "websocket error", "unexpected eof",
        "bad gateway", "gateway timeout", "gateway time-out", "upstream connect error", "网络错误", "网络连接失败", "连接中断",
        "连接超时", "请求超时", "流式传输失败"];
    private static readonly HashSet<string> Placeholders = ["", "Ask Codex to do anything", "Find and fix a bug in @filename",
        "Write tests for @filename", "Summarize recent commits", "Explain this codebase", "Implement {feature}", "Improve documentation in @filename"];

    public FailureReason? Reason(string message)
    {
        var lower = message.ToLowerInvariant();
        if (Excluded.Any(lower.Contains)) return null;
        if (Capacity.Any(lower.Contains)) return FailureReason.Capacity;
        return Network.Any(lower.Contains) ? FailureReason.Network : null;
    }

    public Assessment Assess(Observation observation)
    {
        if (observation.Error is not null) return Assessment.Skip("读取失败，等待重连");
        if (!observation.Agent.AcceptsInput) return Assessment.Skip(observation.Agent.Status switch
        { "working" => "正在运行", "blocked" => "等待人工确认", _ => "状态未知或尚未就绪" });
        if (observation.ForegroundPid is null) return Assessment.Skip("无法确认当前进程");
        return observation.Agent.Kind switch
        { "codex" => Codex(observation.Text), "omp" => Omp(observation), _ => Assessment.Skip("原因未知：尚无此 agent 的可靠解析规则") };
    }

    private Assessment Codex(string text)
    {
        var lines = Clean(text).Split('\n');
        var input = Array.FindLastIndex(lines, l => l.Trim().StartsWith('›'));
        if (input < 0) return Assessment.Skip("无法确认输入框");
        if (!Placeholders.Contains(lines[input].Trim()[1..].Trim())) return Assessment.Skip("输入框已有草稿，暂不发送");
        if (!lines.Skip(input + 1).Where(l => !string.IsNullOrWhiteSpace(l)).All(IsFooter))
            return Assessment.Skip("输入区域不明确，暂不发送");
        var error = input == 0 ? -1 : Array.FindLastIndex(lines, input - 1, input, l => l.StartsWith("■ ") || l.StartsWith(" ■ "));
        if (error < 0) return Assessment.Skip("未发现当前容量或网络错误");
        var continuation = lines.Skip(error + 1).Take(input - error - 1).TakeWhile(l =>
            string.IsNullOrWhiteSpace(l) || l.StartsWith("  ") && !new[] { "•", "›", "└", "│", "```", "■" }.Any(l.Trim().StartsWith)).ToArray();
        if (lines.Skip(error + 1 + continuation.Length).Take(input - error - continuation.Length - 1).Any(l => !string.IsNullOrWhiteSpace(l)))
            return Assessment.Skip("错误之后已有新消息");
        var message = string.Join(" ", new[] { lines[error] }.Concat(continuation.Where(l => !string.IsNullOrWhiteSpace(l)))).Trim();
        if (Reason(message) is not { } reason) return Assessment.Skip("当前停止原因不在自动续接范围");
        var previous = error == 0 ? -1 : Array.FindLastIndex(lines, error - 1, error, l => l.Trim().StartsWith('›'));
        var start = previous >= 0 ? previous : Math.Max(0, error - 8);
        return Assessment.Failure(new(reason, message, Digest(string.Join('\n', lines[start..(error + 1)]) + message)));
    }

    private Assessment Omp(Observation observation)
    {
        var lines = Clean(observation.Text).Split('\n');
        var footer = Array.FindLastIndex(lines, l => l.Trim().StartsWith("π >"));
        if (footer < 0) return Assessment.Skip("无法确认 OMP 输入框");
        if (!lines.Skip(footer + 1).Select(l => l.Trim()).Where(l => l.Length > 0).SequenceEqual(["╰─"]))
            return Assessment.Skip("OMP 输入框已有草稿或布局未知");
        if (observation.Transcript is null) return Assessment.Skip("无法读取 OMP 当前会话记录");
        foreach (var line in observation.Transcript.Split('\n').Reverse().Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var record = document.RootElement;
                if (String(record, "type") != "message") continue;
                if (!record.TryGetProperty("message", out var message) || String(message, "role") != "assistant" ||
                    String(message, "stopReason") != "error" || String(message, "errorMessage") is not { } error ||
                    Reason(error) is not { } reason || String(record, "id") is not { } id)
                    return Assessment.Skip("未发现当前容量或网络错误");
                if (Normalize(error).Length == 0 || !Normalize(Clean(observation.Text)).Contains(Normalize(error), StringComparison.Ordinal))
                    return Assessment.Skip("OMP 记录与当前错误画面不一致");
                return Assessment.Failure(new(reason, error, Digest(id + error)));
            }
            catch (JsonException) { return Assessment.Skip("OMP 会话记录尚未完整写入"); }
        }
        return Assessment.Skip("OMP 会话中未发现当前错误");
    }

    private static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    private static string Clean(string text) => Regex.Replace(text, "\u001B\\[[0-?]*[ -/]*[@-~]", "").Replace("\r", "");
    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool IsFooter(string line)
    {
        var s = line.Trim();
        return s.Length == 0 || s.StartsWith("GPT-") || s.StartsWith("gpt-") || s.Contains("? for shortcuts") ||
            new[] { "New activity ·", "← for agents", "↑ for history", "context left", "Tip:" }.Any(s.StartsWith);
    }
}
