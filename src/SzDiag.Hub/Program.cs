using Microsoft.Extensions.Options;
using SzDiag.Contracts;
using SzDiag.Hub;
using SzDiag.Kb;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<HubOptions>(builder.Configuration.GetSection("Hub"));
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<ISessionStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<HubOptions>>().Value;
    return new SqliteSessionStore(opts.SqliteConnectionString);
});
builder.Services.AddSingleton<IKnowledgeBaseScaffolder>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<HubOptions>>().Value;
    return new KnowledgeBaseScaffolder(opts.KnowledgeBaseRoot);
});
builder.Services.AddHostedService<OfflineSweeper>();
builder.Services.AddSingleton<IAgentCommandSender, SignalRAgentCommandSender>();
builder.Services.AddSingleton<SessionCloser>();
builder.Services.AddSignalR();

var app = builder.Build();

// Инициализация БД при старте.
await app.Services.GetRequiredService<ISessionStore>().InitializeAsync();

// Проверка pre-shared токена на коннекте к хабу.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments(HubRoutes.Path))
    {
        var expected = app.Services.GetRequiredService<IOptions<HubOptions>>().Value.AgentToken;
        var provided = ctx.Request.Headers[HubRoutes.TokenHeader].ToString();
        if (string.IsNullOrEmpty(expected) || provided != expected)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next();
});

app.MapHub<AgentHub>(HubRoutes.Path);
app.MapManagementApi();

app.Run();

// Для WebApplicationFactory в тестах.
public partial class Program { }
