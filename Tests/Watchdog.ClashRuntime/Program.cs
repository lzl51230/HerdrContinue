using System.Diagnostics;
using System.Text.Json;
using Watchdog.Core;
using Watchdog.Infrastructure;

if(args is ["--catalogue"])
{
    var snapshot=await new LiveClashService().SnapshotAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { sources=snapshot.Sources.Select(s=>new {s.Name,count=s.Nodes.Length,s.Error}), current=snapshot.Sources.FirstOrDefault(s=>s.Id==snapshot.CurrentSourceId)?.Name },new JsonSerializerOptions{WriteIndented=true}));
    return;
}
if(args.Length!=2) throw new ArgumentException("Usage: <mihomo executable> <new QA output directory>");
var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
var root=Path.Combine(Path.GetTempPath(),"watchdog-qa-source-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
var endpoint=OperatingSystem.IsWindows() ? new LocalEndpoint(EndpointKind.NamedPipe,@"watchdog-qa-source-"+Guid.NewGuid().ToString("N")) : new(EndpointKind.UnixSocket,Path.Combine(root,"api.sock"));
var control=OperatingSystem.IsWindows()?"external-controller-pipe: '\\\\.\\pipe\\"+endpoint.Address+"'\n":"external-controller-unix: '"+endpoint.Address+"'\n";
var basis="mode: global\nmixed-port: 0\ndns:\n  enable: false\nproxies:\n- name: 日本01\n  type: socks5\n  server: 127.0.0.1\n  port: 9\n"+control;
var config=Path.Combine(root,"config.yaml");await File.WriteAllTextAsync(config,basis);
var start=new ProcessStartInfo(Path.GetFullPath(args[0])) {UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
foreach(var a in new[]{"-d",root,"-f",config})start.ArgumentList.Add(a);
using var core=Process.Start(start)!;var stdout=core.StandardOutput.ReadToEndAsync();var stderr=core.StandardError.ReadToEndAsync();
var http=new LocalHttpTransport();var result=new Dictionary<string,object>();
try {
    for(var i=0;;i++) {
        try {var ready=await http.RequestAsync(endpoint,HttpMethod.Get,"/version");if(ready.Status==200)break;} catch(Exception) when(i<40) {}
        if(i>=40)throw new IOException("Isolated core failed to start");await Task.Delay(100);
    }
    // Both subscriptions deliberately contain the same original node name.
    var definitions=new Dictionary<string,YamlDotNet.RepresentationModel.YamlMappingNode[]>();
    var deserialize=new YamlDotNet.RepresentationModel.YamlStream();deserialize.Load(new StringReader("name: 日本01\ntype: socks5\nserver: 127.0.0.1\nport: 9\n"));
    definitions["B"]=[(YamlDotNet.RepresentationModel.YamlMappingNode)deserialize.Documents[0].RootNode];
    var alias=ClashProfileRepository.RuntimeName("B","日本01");
    var catalogue=new ClashProfileCatalogue(root,"A","test",basis,[new("A","源A",[new("日本01",SourceId:"A")]),new("B","源B",[new(alias,SourceId:"B")])],definitions);
    var payload=ClashProfileRepository.Compose(catalogue,["A","B"]);
    var loaded=await http.RequestAsync(endpoint,HttpMethod.Put,"/configs",JsonSerializer.Serialize(new{payload}));Require(loaded.Status==204,"aggregate reload");
    var proxies=await Proxies();Require(proxies.TryGetProperty(alias,out _),"namespaced node present");Require(proxies.TryGetProperty("日本01",out _),"original node retained");
    var selected=await http.RequestAsync(endpoint,HttpMethod.Put,"/proxies/GLOBAL",JsonSerializer.Serialize(new{name=alias}));Require(selected.Status==204,"cross source select");
    proxies=await Proxies();Require(proxies.GetProperty("GLOBAL").GetProperty("now").GetString()==alias,"selection readback");
    var bad=await http.RequestAsync(endpoint,HttpMethod.Put,"/configs",JsonSerializer.Serialize(new{payload="proxies: invalid"}));Require(bad.Status==400,"invalid configuration rejected");
    proxies=await Proxies();Require(proxies.GetProperty("GLOBAL").GetProperty("now").GetString()==alias,"invalid load preserves live selection");
    var restored=await http.RequestAsync(endpoint,HttpMethod.Put,"/configs",JsonSerializer.Serialize(new{payload=basis}));Require(restored.Status==204,"restore base");
    proxies=await Proxies();Require(!proxies.TryGetProperty(alias,out _),"aggregate removed");Require(await File.ReadAllTextAsync(config)==basis,"source file unchanged");
    result["success"]=true;result["sameNameCrossSourceSelection"]=true;result["invalidLoadPreservedSelection"]=true;result["restoredOriginal"]=true;
} catch(Exception e){result["success"]=false;result["error"]=e.ToString();throw;}
finally {
    if(!core.HasExited)core.Kill(true);await core.WaitForExitAsync();await File.WriteAllTextAsync(Path.Combine(output,"core.log"),await stdout+await stderr);
    await File.WriteAllTextAsync(Path.Combine(output,"result.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
    Directory.Delete(root,true);
}
async Task<JsonElement> Proxies(){var r=await http.RequestAsync(endpoint,HttpMethod.Get,"/proxies");using var d=JsonDocument.Parse(r.Body);return d.RootElement.GetProperty("proxies").Clone();}
static void Require(bool condition,string what){if(!condition)throw new IOException("Failed: "+what);}
