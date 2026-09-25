using System.Net;
using System.Text;
using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

/// <summary>Сервер проверяется голым JSON-RPC: в stateless-режиме `tools/call` отвечает без
/// initialize, ответ — SSE `event: message` / `data: {...}` (проверено на спайке).</summary>
public class DeskMcpServerTests : IAsyncLifetime
{
    private readonly PermissionBroker _broker = new(TimeProvider.System);
    private readonly DeskMcpServer _server = new();
    private readonly HttpClient _http = new();

    public Task InitializeAsync() => _server.StartAsync(_broker);

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private Task<HttpResponseMessage> CallAsync(string key, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _server.EndpointFor(key));
        if (token is not null) req.Headers.Add(DeskMcpServer.TokenHeader, token);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        req.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"permission_prompt","arguments":{"tool_name":"Write","input":{"file_path":"C:\\x.txt"},"tool_use_id":"t1"}}}""",
            Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    private static async Task<JsonElement> VerdictAsync(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var data = body.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        var text = JsonDocument.Parse(data).RootElement.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<PendingPermission> NextRequest()
    {
        var tcs = new TaskCompletionSource<PendingPermission>();
        _broker.Requested += p => tcs.TrySetResult(p);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PermissionPrompt_WaitsForOperator_Allow()
    {
        var next = NextRequest();
        var call = CallAsync("161432", _server.Token);
        var p = await next;
        Assert.Equal("161432", p.Key);                       // ключ сессии — из пути /mcp/<ключ>
        Assert.Equal("Write", p.ToolName);
        Assert.Equal("t1", p.ToolUseId);
        Assert.False(call.IsCompleted);                     // ждёт человека

        _broker.Resolve(p.RequestId, true);
        var v = await VerdictAsync(await call);
        Assert.Equal("allow", v.GetProperty("behavior").GetString());
        Assert.Equal("C:\\x.txt", v.GetProperty("updatedInput").GetProperty("file_path").GetString());
    }

    [Fact]
    public async Task PermissionPrompt_Deny()
    {
        var next = NextRequest();
        var call = CallAsync("161432", _server.Token);
        _broker.Resolve((await next).RequestId, false, "не надо");
        var v = await VerdictAsync(await call);
        Assert.Equal("deny", v.GetProperty("behavior").GetString());
        Assert.Equal("не надо", v.GetProperty("message").GetString());
    }

    [Fact]
    public async Task WrongOrMissingToken_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync("161432", "wrong-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync("161432", null)).StatusCode);
    }

    [Fact]
    public void McpConfig_HasEndpointTokenAndDayTimeout()
    {
        var desk = JsonDocument.Parse(_server.McpConfigJson("161432")).RootElement
            .GetProperty("mcpServers").GetProperty("desk");
        Assert.Equal("http", desk.GetProperty("type").GetString());
        Assert.Equal(_server.EndpointFor("161432"), desk.GetProperty("url").GetString());
        Assert.EndsWith("/mcp/161432", desk.GetProperty("url").GetString());
        Assert.StartsWith("http://127.0.0.1:", desk.GetProperty("url").GetString());
        Assert.Equal(_server.Token, desk.GetProperty("headers").GetProperty(DeskMcpServer.TokenHeader).GetString());
        Assert.Equal(86_400_000, desk.GetProperty("timeout").GetInt32());
    }
}
