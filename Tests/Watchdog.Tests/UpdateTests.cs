using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Watchdog.Infrastructure;
using Xunit;

namespace Watchdog.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData("v1.10.0", "1.9.0", true)]
    [InlineData("v1.6.1", "1.6.1", false)]
    [InlineData("v1.5.0", "1.6.1", false)]
    public void VersionComparisonIsNumeric(string remote, string local, bool expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { tag_name = remote, draft = false, prerelease = false,
            assets = new[] { new { name = "HerdrAutoContinue-win-x64.zip", browser_download_url = "https://github.com/package.zip", digest = (string?)null, size = 12 } } }));
        Assert.Equal(expected, AppUpdates.SelectRelease(json.RootElement, local, "win-x64") is not null);
    }
    [Fact] public void PrereleaseIsExcluded()
    {
        using var json = JsonDocument.Parse("{\"draft\":false,\"prerelease\":true}");
        Assert.Null(AppUpdates.SelectRelease(json.RootElement, "1.0.0", "win-x64"));
    }
    [Fact] public async Task NetworkFailureIsVisibleAndRetryable()
    {
        using var updates = new AppUpdates(new FailureHandler());
        await updates.CheckAsync(); Assert.False(updates.Busy); Assert.Contains("127.0.0.1:7890", updates.Status);
        await updates.CheckAsync(); Assert.Contains("检查失败", updates.Status);
    }
    [Fact] public async Task ConcurrentChecksShareOneRequest()
    {
        var handler = new PendingHandler(); using var updates = new AppUpdates(handler);
        var first = updates.CheckAsync(); await updates.CheckAsync();
        Assert.Equal(1, handler.Count);
        handler.Response.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"draft\":false,\"prerelease\":true}") });
        await first; Assert.False(updates.Busy);
    }
    private sealed class PendingHandler : HttpMessageHandler
    {
        public int Count;
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Count++; return Response.Task; }
    }
    private sealed class FailureHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("offline"); }
    [Fact] public async Task ExplicitProxyOverridesNoProxyAndNeverFallsBack()
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)target.LocalEndpoint).Port}/");
        using var handler = AppUpdates.CreateHandler();
        Assert.Equal(new Uri("http://127.0.0.1:7890"), handler.Proxy!.GetProxy(address));
        Assert.False(handler.Proxy.IsBypassed(address));
        var old = Environment.GetEnvironmentVariable("NO_PROXY");
        Environment.SetEnvironmentVariable("NO_PROXY", "*");
        try
        {
            handler.Proxy = new WebProxy($"http://127.0.0.1:{((IPEndPoint)proxy.LocalEndpoint).Port}", false);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            var request = http.GetAsync(address);
            using var connection = await proxy.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await connection.GetStream().WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            using var response = await request;
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode); Assert.False(target.Pending());
        }
        finally { Environment.SetEnvironmentVariable("NO_PROXY", old); }
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("folder/../../escape")]
    public void UnsafeArchiveIsRejectedBeforeExtraction(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "watchdog-qa-update-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            var zip = Path.Combine(directory, "package.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry(name);
            Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(zip, Path.Combine(directory, "output")));
            Assert.False(Directory.Exists(Path.Combine(directory, "output")));
        }
        finally { Directory.Delete(directory, true); }
    }
}
