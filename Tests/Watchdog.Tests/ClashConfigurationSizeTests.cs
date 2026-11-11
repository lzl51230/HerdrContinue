using System.Text;
using System.Text.Json;
using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class ClashConfigurationSizeTests
{
    private const int Limit = 32 * 1024 * 1024;
    private const string Nodes = "proxies:\n- name: 日本01\n  type: socks5\n  server: 127.0.0.1\n  port: 9\n";

    [Fact]
    public void GrowthAfterLengthCheckCannotBypassLimit()
    {
        using var input = new GrowingStream(new byte[Limit + 100]);
        var error = Assert.Throws<ClashProfileException>(() => ClashProfileRepository.Read(input, "growing.yaml"));
        Assert.Contains("32 MiB", error.Message);
        Assert.Equal(Limit + 1, input.Position);
    }

    private sealed class GrowingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override long Length => 1;
    }

    [Theory]
    [InlineData(16 * 1024 * 1024 + 1)]
    [InlineData(Limit)]
    public void ReadsThroughInclusiveLimit(int bytes)
    {
        var root = TempDirectory();
        try
        {
            var path = Path.Combine(root, "config.yaml");
            // Three UTF-8 bytes per Chinese character: the limit is not a character count.
            var text = new string('中', bytes / 3) + new string('x', bytes % 3);
            File.WriteAllText(path, text);
            Assert.Equal(bytes, new FileInfo(path).Length);
            Assert.Equal(text, ClashProfileRepository.Read(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsOneByteOverLimitWithUsefulError()
    {
        var root = TempDirectory();
        try
        {
            var path = Path.Combine(root, "oversized.yaml");
            using (var file = File.Create(path)) file.SetLength(Limit + 1);
            var error = Assert.Throws<ClashProfileException>(() => ClashProfileRepository.Read(path));
            Assert.Contains("oversized.yaml", error.Message);
            Assert.Contains((Limit + 1).ToString(), error.Message);
            Assert.Contains("32 MiB", error.Message);
            Assert.DoesNotContain(root, error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    public void PreservesBomDetection(string encoding)
    {
        var root = TempDirectory();
        try
        {
            var path = Path.Combine(root, "config.yaml");
            File.WriteAllText(path, Nodes, Encoding.GetEncoding(encoding));
            Assert.Equal(Nodes, ClashProfileRepository.Read(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("work/config.yaml")]
    [InlineData("profiles/b.yaml")]
    [InlineData("work/provider.yaml")]
    public void CatalogueReadsLargeWorkSubscriptionAndProvider(string target)
    {
        var root = CreateCatalogue();
        try
        {
            if (target.EndsWith("provider.yaml"))
                File.WriteAllText(Path.Combine(root, "profiles/b.yaml"), "proxy-providers:\n  test:\n    path: provider.yaml\n");
            File.WriteAllText(Path.Combine(root, target), Nodes + "#" + new string('x', 16 * 1024 * 1024) + "\n");
            var catalogue = new ClashProfileRepository(root).ReadCatalogue(Clash("日本01", names: ["日本01"]))!;
            Assert.Equal(2, catalogue.Sources.Count);
            Assert.All(catalogue.Sources, source => Assert.Single(source.Nodes));
            using (var file = File.Create(Path.Combine(root, target))) file.SetLength(Limit + 1);
            if (target == "work/config.yaml")
                Assert.Throws<ClashProfileException>(() => new ClashProfileRepository(root).ReadCatalogue(Clash("日本01", names: ["日本01"])));
            else
            {
                catalogue = new ClashProfileRepository(root).ReadCatalogue(Clash("日本01", names: ["日本01"]))!;
                Assert.Single(catalogue.Sources.Single(s => s.Id == catalogue.CurrentId).Nodes);
                Assert.Empty(catalogue.Sources.Single(s => s.Id != catalogue.CurrentId).Nodes);
                Assert.Contains("32 MiB", JsonSerializer.Serialize(catalogue.Sources));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory, MemberData(nameof(TransportTests.Transports), MemberType = typeof(TransportTests))]
    public async Task LargeConfigurationLoadsBacksUpAndRestores(EndpointKind kind)
    {
        var root = CreateCatalogue();
        try
        {
            // A retained YAML value, not a comment: JSON escaping expands this past 32 MiB.
            var marker = new string('中', 6 * 1024 * 1024);
            var yaml = "mode: rule\nmixed-port: 7890\nx-test: " + marker + "\n" + Nodes;
            var path = Path.Combine(root, "work/config.yaml");
            File.WriteAllText(path, yaml);
            var state = Clash("日本01", names: ["日本01"]);
            var loads = 0;
            await using var fixture = new HttpFixture(kind, request =>
            {
                if (request.Resource == "/version") return new(200, "{\"meta\":true}");
                if (request.Resource == "/configs" && request.Method == "GET") return new(200, "{\"mode\":\"global\",\"mixed-port\":7890}");
                if (request.Resource == "/proxies") return new(200, JsonSerializer.Serialize(new { proxies = state.Nodes }, WireJson.Options));
                if (request.Resource == "/proxies/GLOBAL" && request.Method == "PUT")
                {
                    using var document = JsonDocument.Parse(request.Body);
                    Assert.Equal("日本01", document.RootElement.GetProperty("name").GetString());
                    return new(204, "");
                }
                if (request.Resource == "/configs" && request.Method == "PUT")
                {
                    Assert.True(Encoding.UTF8.GetByteCount(request.Body) > Limit);
                    using var document = JsonDocument.Parse(request.Body);
                    var config = ClashProfileRepository.Parse(document.RootElement.GetProperty("payload").GetString()!);
                    Assert.Equal(marker, ClashProfileRepository.Text(config, "x-test"));
                    Assert.Equal("global", ClashProfileRepository.Text(config, "mode"));
                    Assert.Equal("7890", ClashProfileRepository.Text(config, "mixed-port"));
                    var names = ClashProfileRepository.Items(config, "proxies").Select(n => ClashProfileRepository.Text(n, "name")!).ToArray();
                    state = Clash("日本01", names: names);
                    loads++;
                    return new(204, "");
                }
                return new(404, "{}");
            });
            var service = new LiveClashService(new FixedDiscovery(fixture.Endpoint), profiles: new(root), runtimeStateDirectory: Path.Combine(root, "state"));
            var before = await service.SnapshotAsync(default);
            await service.PrepareSourcesAsync(new(["JP"], before.Sources.Select(s => s.Id)), default);
            Assert.Equal(2, state.Nodes.Values.Count(n => n.IsProxyLeaf));
            using (var backup = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(root, "state"))))))
                Assert.Equal(yaml, backup.RootElement.GetProperty("payload").GetString());
            await service.RestoreSourcesAsync(default);
            Assert.Equal(2, loads);
            Assert.Single(state.Nodes.Values, n => n.IsProxyLeaf);
            Assert.Equal(yaml, File.ReadAllText(path));
            Assert.Equal(Nodes, File.ReadAllText(Path.Combine(root, "profiles/b.yaml")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateCatalogue()
    {
        var root = TempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "profiles"));
        Directory.CreateDirectory(Path.Combine(root, "work"));
        File.WriteAllText(Path.Combine(root, "profile.yaml"), "current: a\nitems:\n- id: a\n  name: A\n- id: b\n  name: B\n");
        File.WriteAllText(Path.Combine(root, "config.yaml"), "diffWorkDir: false\n");
        File.WriteAllText(Path.Combine(root, "work/config.yaml"), Nodes);
        File.WriteAllText(Path.Combine(root, "profiles/b.yaml"), Nodes);
        return root;
    }
}
