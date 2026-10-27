using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Watchdog.Core;
using Watchdog.Infrastructure;

Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["--instance-qa", var scope] && scope.StartsWith("watchdog-qa-", StringComparison.Ordinal))
{
    using var instance = new AppInstance(scope);
    if (!instance.IsPrimary) { await instance.NotifyAsync(); Console.WriteLine("secondary"); return 0; }
    instance.SetActivationHandler(() => Console.WriteLine("activated"));
    Console.WriteLine("primary");
    await Console.In.ReadLineAsync();
    return 0;
}
var address = Environment.GetEnvironmentVariable("HERDR_SOCKET_PATH");
var pane = Environment.GetEnvironmentVariable("HERDR_PANE_ID");
if (address is null || pane is null || !address.Contains("watchdog-qa-", StringComparison.Ordinal) || args.Length != 1) return 2;
var endpoint = LocalEndpoint.ForCurrentPlatform(address);
var rpc = new SocketRpc();
async Task Report(string state, CancellationToken ct = default) => await rpc.CallAsync(endpoint, "pane.report_agent", new Dictionary<string, object?>
{ ["pane_id"] = pane, ["source"] = "watchdog-qa", ["agent"] = "codex", ["state"] = state }, cancellationToken: ct);
Console.Write("\u001b[2J\u001b[H› Isolated watchdog test\n\n■ Selected model is at capacity. Please try a different model.\n\n› Ask Codex to do anything\n  GPT-6.1-Sol high · ~/project\n  ? for shortcuts\n");
await Report("idle");
using var waiting = new CancellationTokenSource();
var heartbeat = Task.Run(async () =>
{
    try { while (!waiting.IsCancellationRequested) { await Task.Delay(250, waiting.Token); await Report("idle", waiting.Token); } }
    catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
});
var watch = Stopwatch.StartNew();
var input = await Console.In.ReadLineAsync();
waiting.Cancel(); await heartbeat;
if (input is null) return 5;
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
await File.WriteAllTextAsync(args[0], JsonSerializer.Serialize(new { receivedContinueAndNewline = input == "继续", elapsed = watch.Elapsed.TotalSeconds, count = 1 }) + "\n");
await Report("working");
Console.Write("\u001b[2J\u001b[H• QA agent received the continuation and resumed.\n");
await Task.Delay(1000); Console.Write("\n› Ask Codex to do anything\n"); await Report("idle");
while (await Console.In.ReadLineAsync() is not null) await File.AppendAllTextAsync(args[0], "UNEXPECTED_DUPLICATE\n");
return 0;
