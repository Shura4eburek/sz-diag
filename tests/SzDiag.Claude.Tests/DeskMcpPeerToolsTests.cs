using System.Text;
using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class DeskMcpPeerToolsTests : IAsyncLifetime
{
    private readonly SessionHarness _h = new();
    private readonly FakePeerDirectory _dir = new();
    private readonly DeskMcpServer _server = new();
    private readonly HttpClient _http = new();

    public Task InitializeAsync()
        => _server.StartAsync(_h.Broker, new PeerExchange(_h.Manager, _dir, PeerLimits.Default, TimeProvider.System));

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        _h.Dispose();
    }

    private async Task<JsonElement> CallAsync(string key, string tool, string args)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _server.EndpointFor(key));
        req.Headers.Add(DeskMcpServer.TokenHeader, _server.Token);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        req.Content = new StringContent(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{args}}}}}""",
            Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var data = body.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        return JsonDocument.Parse(data).RootElement.GetProperty("result").Clone();
    }

    private static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;
    private static bool IsError(JsonElement result) => result.TryGetProperty("isError", out var e) && e.GetBoolean();

    [Fact]
    public async Task Peers_ListsOthersForCallingKey()
    {
        _dir.All.Add(new PeerInfo("161501", "Ryzen 7 9800X3D", "CPU Ryzen 7 9800X3D"));
        var r = await CallAsync("161432", "peers", "{}");
        Assert.False(IsError(r));
        Assert.Contains("161501 · Ryzen 7 9800X3D · похоже: CPU Ryzen 7 9800X3D", Text(r));
    }

    [Fact]
    public async Task AskPeer_Summary()
    {
        _dir.Summaries["161501"] = "СЗ 161501 · память меняли";
        var r = await CallAsync("161432", "ask_peer", """{"key":"161501","question":"что нашли?"}""");
        Assert.False(IsError(r));
        Assert.StartsWith("СЗ 161501 · память меняли", Text(r));
    }

    [Fact]
    public async Task AskPeer_Failure_IsToolError()
    {
        // Спека, «Ошибки»: лимит/таймаут/глубина — ошибка инструмента с причиной, Claude решает сам.
        var r = await CallAsync("161432", "ask_peer", """{"key":"161501","question":"?","live":true}""");
        Assert.True(IsError(r));
        Assert.Contains("нет активной сессии", Text(r));
    }
}
