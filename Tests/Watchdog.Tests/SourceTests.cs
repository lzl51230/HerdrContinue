using Watchdog.Core;
using Watchdog.Infrastructure;
using Xunit;
using static Watchdog.Tests.TestData;

namespace Watchdog.Tests;

public sealed class SourceTests
{
    [Fact] public async Task SourcesPersistIndependentlyOfRegionPriority()
    {
        var root=TempDirectory();
        try {
            var store=new RegionPreferencesStore(Path.Combine(root,"settings.json"));
            await store.SaveAsync(new(["HK","JP"],["B","A","B"]));
            var p=(await store.LoadAsync()).Preferences;
            Assert.Equal(new[]{"HK","JP"},p.PriorityRegionIds); Assert.Equal(new[]{"A","B"},p.SelectedSourceIds);
            Assert.False(p.HasSameOrder(new(["HK","JP"],["A"])));
        } finally { Directory.Delete(root,true); }
    }
    [Fact] public async Task LegacyPreferencesKeepRegionsAndDoNotDefaultToEverySource()
    {
        var root=TempDirectory();
        try {
            var path=Path.Combine(root,"settings.json"); await File.WriteAllTextAsync(path,"{\"version\":1,\"priorityRegionIds\":[\"SG\",\"JP\"]}");
            var p=(await new RegionPreferencesStore(path).LoadAsync()).Preferences;
            Assert.Equal(new[]{"SG","JP"},p.PriorityRegionIds);Assert.Null(p.SelectedSourceIds);
            Assert.True(p.Allows(new("日本",SourceId:"current"),"current"));
            Assert.False(p.Allows(new("日本",SourceId:"other"),"current"));
        } finally { Directory.Delete(root,true); }
    }
    [Fact] public async Task SameRegionAcrossSourcesStartsTogetherAndCanFailOverAcrossSources()
    {
        var a="source-A 日本01";var b="source-B 日本01";var excluded="source-C 日本01";
        var state=Clash(a,names:[a,b,excluded]);
        state=state with { CurrentSourceId="A", Nodes=state.Nodes.ToDictionary(p=>p.Key,p=>p.Value.IsProxyLeaf ? p.Value with {SourceId=p.Key==a?"A":p.Key==b?"B":"C",OriginalName="日本01"} : p.Value) };
        var barrier=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var started=0;
        var service=new FakeClash {State=state,Healthy=false,HealthyNodes=[b], Measure=async (node,url,ct)=>{
            if(Interlocked.Increment(ref started)==2) barrier.TrySetResult();
            await barrier.Task.WaitAsync(ct);return node==a?10:20;
        }};
        await using var proxy=new ProxyMonitor(service,preferences:new(["JP"],["A","B"]));proxy.Start(false);
        await proxy.CheckNowAsync();await proxy.CheckNowAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2,service.MaximumConcurrent);Assert.DoesNotContain(excluded,service.Tested);
        Assert.Equal(new[]{b},service.Selected);Assert.Equal(ProxyPhase.Healthy,proxy.Status.Phase);
    }
    [Fact] public void OriginalNodeNameControlsRegionInsteadOfSourcePrefix()
    { Assert.Equal("HK",new ClashNode("日本机场 香港01",OriginalName:"香港01").Region?.Id); }
    [Fact] public void EmptySourceSelectionAllowsNoSourceNodes()
    { Assert.False(new ProxyRegionPreferences(["JP"],[]).Allows(new("日本01",SourceId:"A"),"A")); }
    [Fact] public void AggregationNamespacesSameNameAndLeavesBaseSettingsIntact()
    {
        var root=TempDirectory();
        try {
            Directory.CreateDirectory(Path.Combine(root,"profiles"));Directory.CreateDirectory(Path.Combine(root,"work"));
            File.WriteAllText(Path.Combine(root,"profile.yaml"),"current: a\nitems:\n- id: a\n  name: 第一源\n- id: b\n  name: 第二源\n");
            File.WriteAllText(Path.Combine(root,"config.yaml"),"diffWorkDir: false\n");
            var yaml="mode: global\nmixed-port: 7890\ndns:\n  enable: false\nproxies:\n- name: 日本01\n  type: socks5\n  server: 127.0.0.1\n  port: 9999\n";
            File.WriteAllText(Path.Combine(root,"work/config.yaml"),yaml);File.WriteAllText(Path.Combine(root,"profiles/b.yaml"),yaml);
            var c=new ClashProfileRepository(root).ReadCatalogue(Clash("日本01",names:["日本01"]))!;
            Assert.Equal(2,c.Sources.Count);var second=c.Sources.Single(s=>s.Name=="第二源");
            var result=ClashProfileRepository.Compose(c,[second.Id]);
            Assert.Contains(ClashProfileRepository.RuntimeName(second.Id,"日本01"),result);
            Assert.Contains("mixed-port: 7890",result);Assert.Equal(yaml,File.ReadAllText(Path.Combine(root,"work/config.yaml")));
            Assert.NotEqual(c.Sources[0].Nodes[0].Name,second.Nodes[0].Name);
        } finally {Directory.Delete(root,true);}
    }
    [Theory, MemberData(nameof(TransportTests.Transports), MemberType=typeof(TransportTests))]
    public async Task LiveServiceLoadsCrossSourceNodesAndRestoresBase(EndpointKind kind)
    {
        var root=TempDirectory();
        try {
            Directory.CreateDirectory(Path.Combine(root,"profiles"));Directory.CreateDirectory(Path.Combine(root,"work"));
            File.WriteAllText(Path.Combine(root,"profile.yaml"),"current: a\nitems:\n- id: a\n  name: 源A\n- id: b\n  name: 源B\n");
            File.WriteAllText(Path.Combine(root,"config.yaml"),"diffWorkDir: false\n");
            const string yaml="mode: rule\nmixed-port: 7890\nproxies:\n- name: 日本01\n  type: socks5\n  server: 127.0.0.1\n  port: 9\n";
            File.WriteAllText(Path.Combine(root,"work/config.yaml"),yaml);File.WriteAllText(Path.Combine(root,"profiles/b.yaml"),yaml);
            var state=Clash("日本01",names:["日本01"]);
            await using var fixture=new HttpFixture(kind,request=>{
                if(request.Resource=="/version")return new(200,"{\"meta\":true}");
                if(request.Resource=="/configs" && request.Method=="GET")return new(200,"{\"mode\":\"global\",\"mixed-port\":7890}");
                if(request.Resource=="/proxies")return new(200,System.Text.Json.JsonSerializer.Serialize(new{proxies=state.Nodes},WireJson.Options));
                if(request.Resource=="/configs" && request.Method=="PUT") {
                    using var d=System.Text.Json.JsonDocument.Parse(request.Body);
                    var stream=new YamlDotNet.RepresentationModel.YamlStream();stream.Load(new StringReader(d.RootElement.GetProperty("payload").GetString()!));
                    var mapping=(YamlDotNet.RepresentationModel.YamlMappingNode)stream.Documents[0].RootNode;
                    Assert.Equal("global", ((YamlDotNet.RepresentationModel.YamlScalarNode)mapping.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("mode")]).Value);
                    var names=((YamlDotNet.RepresentationModel.YamlSequenceNode)mapping.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("proxies")]).Children
                        .Cast<YamlDotNet.RepresentationModel.YamlMappingNode>().Select(n=>((YamlDotNet.RepresentationModel.YamlScalarNode)n.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("name")]).Value!).ToArray();
                    state=Clash(names.Contains(state.Selected!)?state.Selected!:names[0],names:names);return new(204,"");
                }
                if(request.Method=="PUT" && request.Resource=="/proxies/GLOBAL") {
                    using var d=System.Text.Json.JsonDocument.Parse(request.Body);var name=d.RootElement.GetProperty("name").GetString()!;
                    state=state with {Nodes=state.Nodes.ToDictionary(p=>p.Key,p=>p.Key=="GLOBAL"?p.Value with{Now=name}:p.Value)};return new(204,"");
                }
                return new(404,"{}");
            });
            var service=new LiveClashService(new FixedDiscovery(fixture.Endpoint),profiles:new(root),runtimeStateDirectory:Path.Combine(root,"state"));
            var before=await service.SnapshotAsync(default);Assert.Equal(2,before.Sources.Count);
            var policy=new ProxyRegionPreferences(["JP"],before.Sources.Select(s=>s.Id));
            await service.PrepareSourcesAsync(policy,default);
            var after=await service.SnapshotAsync(default);var foreign=after.Candidates.Single(n=>n.SourceName=="源B");
            await service.SelectAsync(foreign.Name,after,default);
            Assert.Equal(foreign.Name,(await service.SnapshotAsync(default)).Selected);
            File.Delete(Path.Combine(root,"profiles/b.yaml"));
            await Assert.ThrowsAsync<IOException>(()=>service.PrepareSourcesAsync(new(["JP"],[before.CurrentSourceId!]),default));
            Assert.Equal(foreign.Name,(await service.SnapshotAsync(default)).Selected);
            await service.RestoreSourcesAsync(default);
            Assert.Equal("日本01",(await service.SnapshotAsync(default)).Selected);
            Assert.Equal(yaml,File.ReadAllText(Path.Combine(root,"work/config.yaml")));
        } finally {Directory.Delete(root,true);}
    }

    [Fact] public async Task FailedSourceSaveRestoresPreviousSourcePolicy()
    {
        var service=new PreparingClash(); await using var proxy=new ProxyMonitor(service,preferences:new(["JP"],["A"]));
        await Assert.ThrowsAsync<IOException>(()=>proxy.ConfigureAsync(new(["JP"],["B"]),()=>throw new IOException("disk full"),default));
        Assert.Equal(new[]{"B","A"},service.Prepared);Assert.Equal(new[]{"A"},proxy.Preferences.SelectedSourceIds);
    }
    [Fact] public async Task RegionOnlySaveDoesNotRequireReloadingClash()
    {
        var service=new PreparingClash();await using var proxy=new ProxyMonitor(service,preferences:new(["JP"],["A"]));var saved=false;
        await proxy.ConfigureAsync(new(["HK","JP"],["A"]),()=>{saved=true;return Task.CompletedTask;},default);
        Assert.True(saved);Assert.Empty(service.Prepared);Assert.Equal(new[]{"HK","JP"},proxy.Preferences.PriorityRegionIds);
    }
    private sealed class PreparingClash : IClashService
    {
        public List<string> Prepared=[];
        public Task PrepareSourcesAsync(ProxyRegionPreferences p,CancellationToken ct,Action? changing=null) {Prepared.Add(string.Join(",",p.SelectedSourceIds ?? []));return Task.CompletedTask;}
        public Task<ClashSnapshot> SnapshotAsync(CancellationToken ct)=>Task.FromResult(Clash());
        public Task<ProxyProbe> ProbeProxyAsync(CancellationToken ct)=>Task.FromResult(new ProxyProbe(true));
        public Task<int> DelayAsync(string n,LocalEndpoint e,string u,CancellationToken ct)=>throw new InvalidOperationException();
        public Task SelectAsync(string n,ClashSnapshot s,CancellationToken ct)=>Task.CompletedTask;
    }

}
