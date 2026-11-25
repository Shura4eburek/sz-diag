// Живая проверка T044: libzt-нода агента → настоящий hub по адресу в сервисной сети.
//   zt-agent-e2e <nwid> <hubUrl> <agentToken>
using Microsoft.AspNetCore.SignalR.Client;
using SzDiag.Agent.Zt;

var storage = ZtStorage.Throwaway();
using var node = LibztNode.Start(storage, args[0], TimeSpan.FromSeconds(60), Console.WriteLine)
    ?? throw new Exception("нода не поднялась");
node.WipeOnDispose = true;
var t = new ZtHubTransport(node);
using (var http = t.CreateHttpClient(args[1]))
    Console.WriteLine($"/healthz: {(await http.GetAsync("/healthz")).StatusCode}");
using (var http = t.CreateHttpClient(args[1]))
    Console.WriteLine($"/api/sessions с ZT: {(int)(await http.GetAsync("/api/sessions")).StatusCode}");
await using var conn = new HubConnectionBuilder()
    .WithUrl(args[1].TrimEnd('/') + "/agents", o => { o.Headers["X-SzDiag-Token"] = args[2]; t.Configure(o); })
    .Build();
await conn.StartAsync();
Console.WriteLine($"SignalR: {conn.State}, id {conn.ConnectionId}");
await conn.StopAsync();
Console.WriteLine($"storage: {storage}");
