using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;

namespace Watchdog.Tests;

public sealed class ProxyProbeTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SamePhysicalConnectionIsReusedWithoutCredentials(bool tls)
    {
        var time = new ManualTime();
        await using var server = new ProbeServer(tls);
        await using var probe = server.Probe(time);
        foreach (var second in new[] { 0, 2, 9, 18, 27, 36, 45, 54, 60 })
        {
            time.Now = second;
            var result = await probe.HeartbeatAsync(default);
            Assert.True(result.Healthy, result.Detail); Assert.Equal(second, result.ConnectionSeconds);
        }
        Assert.Equal(1, server.Connections);
        Assert.All(server.Requests, headers =>
        {
            Assert.DoesNotContain("Authorization:", headers, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Cookie:", headers, StringComparison.OrdinalIgnoreCase);
        });
        await probe.ResetAsync(); time.Now = 61;
        Assert.Equal(0, (await probe.HeartbeatAsync(default)).ConnectionSeconds);
        Assert.Equal(2, server.Connections);
    }

    [Theory] [InlineData(200)] [InlineData(302)] [InlineData(403)] [InlineData(429)] [InlineData(503)]
    public async Task UnexpectedStatusFailsWithoutFollowingRedirect(int status)
    {
        await using var server = new ProbeServer(status: status);
        await using var probe = server.Probe();
        var result = await probe.HeartbeatAsync(default);
        Assert.False(result.Healthy); Assert.Contains(status.ToString(), result.Detail);
        Assert.Single(server.Requests);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SilentConnectionTimesOutAtTenSecondsAndCancellationClosesSession(bool partialBody)
    {
        var time = new ManualTime();
        await using var server = new ProbeServer(hang: true, partialBody: partialBody);
        await using var probe = server.Probe(time);
        var checking = probe.HeartbeatAsync(default);
        await TestData.Eventually(() => server.Requests.Count == 1);
        time.Now = 9.999; Assert.False(checking.IsCompleted);
        time.Now = 10;
        Assert.False((await checking.WaitAsync(TimeSpan.FromSeconds(3))).Healthy);
        using var cancellation = new CancellationTokenSource();
        checking = probe.HeartbeatAsync(cancellation.Token);
        await TestData.Eventually(() => server.Requests.Count == 2); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking);
    }

    [Fact] public async Task ServerClosureAndAutomaticReconnectDoNotAccumulateAge()
    {
        var time = new ManualTime();
        await using var server = new ProbeServer(closeAfterResponse: true);
        await using var probe = server.Probe(time);
        Assert.True((await probe.HeartbeatAsync(default)).Healthy);
        time.Now = 2;
        var result = await probe.HeartbeatAsync(default);
        Assert.False(result.Healthy); Assert.Equal(0, result.ConnectionSeconds);
        time.Now = 3; Assert.Equal(0, (await probe.HeartbeatAsync(default)).ConnectionSeconds);
    }

    [Fact] public async Task InvalidTlsCertificateIsRejected()
    {
        await using var server = new ProbeServer(tls: true);
        await using var probe = new OpenAiConnectionProbe(server.Address, server.Target);
        Assert.False((await probe.HeartbeatAsync(default)).Healthy);
        Assert.Empty(server.Requests);
    }

    private sealed class ProbeServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentBag<Task> handlers = [];
        private readonly Task accepting;
        private readonly X509Certificate2 certificate;
        public int Connections;
        public ConcurrentQueue<string> Requests { get; } = [];
        public Uri Address { get; }
        public Uri Target { get; }
        public ProbeServer(bool tls = false, int status = 401, bool hang = false, bool closeAfterResponse = false, bool partialBody = false)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=watchdog.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("watchdog.invalid");
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            listener.Start(); Address = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            Target = new($"{(tls ? "https" : "http")}://watchdog.invalid/test");
            accepting = AcceptAsync();
            async Task AcceptAsync()
            {
                try { while (!lifetime.IsCancellationRequested) handlers.Add(HandleAsync(await listener.AcceptTcpClientAsync(lifetime.Token))); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
            async Task HandleAsync(TcpClient socket)
            {
                Interlocked.Increment(ref Connections);
                using (socket)
                {
                    try
                    {
                        Stream stream = socket.GetStream();
                        if (tls)
                        {
                            while ((await LocalFixture.ReadLineAsync(stream, lifetime.Token)).Length > 0) { }
                            await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), lifetime.Token);
                            var ssl = new SslStream(stream);
                            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, lifetime.Token);
                            stream = ssl;
                        }
                        await using (stream)
                        {
                            while (!lifetime.IsCancellationRequested)
                            {
                                var first = await LocalFixture.ReadLineAsync(stream, lifetime.Token);
                                if (first.Length == 0) return;
                                var headers = first + "\r\n"; string line;
                                while ((line = await LocalFixture.ReadLineAsync(stream, lifetime.Token)).Length > 0) headers += line + "\r\n";
                                Requests.Enqueue(headers);
                                if (hang)
                                {
                                    if (partialBody) await stream.WriteAsync("HTTP/1.1 401 Test\r\nContent-Length: 2\r\n\r\n{"u8.ToArray(), lifetime.Token);
                                    await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token); return;
                                }
                                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: 2\r\nConnection: keep-alive\r\nSet-Cookie: secret=must-not-return\r\nLocation: http://watchdog.invalid/redirect\r\n\r\n{{}}"), lifetime.Token);
                                if (closeAfterResponse) return;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                    catch (IOException) { }
                    catch (AuthenticationException) { }
                }
            }
        }
        public OpenAiConnectionProbe Probe(TimeProvider? time = null)
        {
            var probe = new OpenAiConnectionProbe(Address, Target, time);
            probe.TlsOptions.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck
            };
            probe.TlsOptions.CertificateChainPolicy.CustomTrustStore.Add(certificate);
            return probe;
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync(); await accepting; await Task.WhenAll(handlers);
            listener.Stop(); certificate.Dispose(); lifetime.Dispose();
        }
    }
}
