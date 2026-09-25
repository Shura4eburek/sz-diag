using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    public async Task StartAsync(PermissionBroker broker, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(broker);
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

[McpServerToolType]
public sealed class DeskTools(PermissionBroker broker, IHttpContextAccessor http)
{
    [McpServerTool(Name = "permission_prompt"), Description("Запрос разрешения на инструмент у оператора SzDiag Desk")]
    public async Task<string> PermissionPrompt(string tool_name, JsonElement input, string? tool_use_id = null,
        CancellationToken ct = default)
    {
        var key = http.HttpContext?.Request.RouteValues["key"] as string ?? "";
        var verdict = await broker.AskAsync(key, tool_name, input, tool_use_id, ct).ConfigureAwait(false);
        return verdict.ToJson();
    }
}
