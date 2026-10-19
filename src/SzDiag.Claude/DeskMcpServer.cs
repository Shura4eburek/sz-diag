using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SzDiag.Claude;

/// <summary>MCP-сервер Desk на 127.0.0.1 со случайным портом. У каждой сессии свой путь
/// `/mcp/&lt;ключ&gt;` — сервер знает, кто зовёт (спайк: путь доезжает, ключ виден в RouteValues).
/// Stateless: запрос разрешения держится открытым, пока человек не ответит.</summary>
public sealed class DeskMcpServer : IAsyncDisposable
{
    public const string TokenHeader = "X-Desk-Token";
    public const string ServerName = "desk";

    /// <summary>Сутки: без поля timeout claude обрывает тулзу после 300 с молчания (спайк),
    /// а разрешение ждёт человека сколько угодно.</summary>
    public const int ToolTimeoutMs = 86_400_000;

    private WebApplication? _app;

    /// <summary>Токен на запуск Desk: MCP-порт слушает localhost, но к нему может постучаться
    /// любой процесс бокса.</summary>
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public string? BaseUrl { get; private set; }

    public async Task StartAsync(PermissionBroker broker, PeerExchange? peers = null, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(broker);
        builder.Services.AddSingleton(new PeerHolder(peers));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<DeskTools>();

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers[TokenHeader] != Token)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(ctx);
        });
        app.MapMcp("/mcp/{key}");
        await app.StartAsync(ct).ConfigureAwait(false);
        BaseUrl = app.Urls.First().TrimEnd('/');
        _app = app;
    }

    public string EndpointFor(string key) => $"{BaseUrl}/mcp/{Uri.EscapeDataString(key)}";

    public string McpConfigJson(string key) => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            [ServerName] = new
            {
                type = "http",
                url = EndpointFor(key),
                headers = new Dictionary<string, string> { [TokenHeader] = Token },
                timeout = ToolTimeoutMs,
            },
        },
    });

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }
}

/// <summary>Обмен между сессиями может быть выключен (тесты разрешений, ядро без Desk).</summary>
public sealed record PeerHolder(PeerExchange? Exchange);

[McpServerToolType]
public sealed class DeskTools(PermissionBroker broker, PeerHolder peers, IHttpContextAccessor http)
{
    private const string Off = "обмен между сессиями в этом Desk выключен";

    private string Key => http.HttpContext?.Request.RouteValues["key"] as string ?? "";

    [McpServerTool(Name = "permission_prompt"), Description("Запрос разрешения на инструмент у оператора SzDiag Desk")]
    public async Task<string> PermissionPrompt(string tool_name, JsonElement input, string? tool_use_id = null,
        CancellationToken ct = default)
    {
        var verdict = await broker.AskAsync(Key, tool_name, input, tool_use_id, ct).ConfigureAwait(false);
        return verdict.ToJson();
    }

    [McpServerTool(Name = "peers"), Description("Другие СЗ в работе с сессией Desk: железо одной строкой и чем похожи на твою машину")]
    public string Peers() => peers.Exchange?.ListPeers(Key) ?? Off;

    [McpServerTool(Name = "ask_peer"), Description(
        "Спросить соседнюю СЗ. Без live — выжимка её kb (діагностика + хвост журнала): даром и без отвлечения соседа. " +
        "live=true — вопрос уходит сессии соседа, ответ — её финальный текст хода (лимит в час, таймаут, глубина 1).")]
    public async Task<CallToolResult> AskPeer(string key, string question, bool live = false, CancellationToken ct = default)
    {
        var r = peers.Exchange is { } ex
            ? await ex.AskAsync(Key, key, question, live, ct).ConfigureAwait(false)
            : PeerReply.Fail(Off);
        return new CallToolResult { IsError = !r.Ok, Content = [new TextContentBlock { Text = r.Text }] };
    }
}
