using Microsoft.Extensions.Options;
using Spectre.Console;
using SzDiag.Contracts;
using SzDiag.Hub;
using SzDiag.Kb;

var builder = WebApplication.CreateBuilder(args);

// Читать appsettings.json рядом с exe независимо от текущего каталога запуска.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);

// Адрес прослушивания: из конфига "Urls" (для standalone-exe), иначе 0.0.0.0:5099.
var listenUrls = (builder.Configuration["Urls"] ?? "http://0.0.0.0:5099")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.WebHost.UseUrls(listenUrls);

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
builder.Services.AddSingleton<IReportStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<HubOptions>>().Value;
    return new KbReportStore(opts.KnowledgeBaseRoot);
});
builder.Services.AddHostedService<OfflineSweeper>();
builder.Services.AddHostedService(sp =>
    new HubDiscoveryResponder(sp.GetRequiredService<IOptions<HubOptions>>()));
builder.Services.AddSingleton<IAgentCommandSender, SignalRAgentCommandSender>();
builder.Services.AddSingleton<SessionCloser>();
builder.Services.AddSingleton<TestRunTrigger>();
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 10 * 1024 * 1024;
    // Внутренний сервис в доверенной сети — отдаём агенту реальный текст ошибки.
    o.EnableDetailedErrors = true;
});

var app = builder.Build();

// Инициализация БД при старте.
await app.Services.GetRequiredService<ISessionStore>().InitializeAsync();

// Баннер со сводкой конфигурации — печатается после того, как Kestrel начал слушать.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var hubOpts = app.Services.GetRequiredService<IOptions<HubOptions>>().Value;
    var panel = new Panel(new Rows(
            new Markup($"[grey]слушает:[/] {Markup.Escape(string.Join(", ", listenUrls))}"),
            new Markup($"[grey]kb:[/] {Markup.Escape(hubOpts.KnowledgeBaseRoot)}   [grey]db:[/] {Markup.Escape(hubOpts.SqliteConnectionString)}")))
        .Header("[bold]sz-diag hub[/]")
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Grey)
        .Padding(1, 0);
    AnsiConsole.Write(panel);
});

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
