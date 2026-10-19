using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Агент сообщает hub, чем машина доступна сейчас. Отдельный метод, а не поле
/// heartbeat: имя quick tunnel'а меняется в течение сессии (после ребута оно другое), а
/// ломать сигнатуру <c>Heartbeat(string)</c> нельзя — её зовут агенты старых сборок.</summary>
public class AgentHubReportAccessTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-ara-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-ara-{Guid.NewGuid():N}");

    public AgentHubReportAccessTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .WithoutSystemLogging());
    }

    private HubConnection BuildConnection()
    {
        var handler = _factory.Server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "agents"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Headers[HubRoutes.TokenHeader] = "test-token";
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    [Fact]
    public async Task ReportAccess_кладёт_имя_туннеля_в_реестр()
    {
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();

        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("162003", "PC-1"));
        await conn.InvokeAsync(HubRoutes.ReportAccess, new AccessReportRequest("162003",
            "aaa-bbb.trycloudflare.com", AccessMode.Tunnel, "ssh-ed25519 AAAAC3Nz"));

        var info = registry.TryGetInfo("162003")!;
        Assert.Equal("aaa-bbb.trycloudflare.com", info.AccessHost);
        Assert.Equal(AccessMode.Tunnel, info.AccessMode);
        Assert.Equal("ssh-ed25519 AAAAC3Nz", info.SshHostKeyFingerprint);
    }

    [Fact]
    public async Task ReportAccess_обновляет_имя_после_ребута()
    {
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();

        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("162010", "PC-2"));
        await conn.InvokeAsync(HubRoutes.ReportAccess,
            new AccessReportRequest("162010", "первое.trycloudflare.com", AccessMode.Tunnel));
        await conn.InvokeAsync(HubRoutes.ReportAccess,
            new AccessReportRequest("162010", "второе.trycloudflare.com", AccessMode.Tunnel));

        Assert.Equal("второе.trycloudflare.com", registry.TryGetInfo("162010")!.AccessHost);
    }

    [Fact]
    public async Task ReportAccess_по_мусорному_номеру_ничего_не_пишет()
    {
        // Токен агента общий на всех (модель угроз: клиент — наименее доверенное устройство),
        // поэтому Sz из тела нельзя принимать как есть — соседние пути это уже проверяют.
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();
        const string мусор = @"..\..\Users\Public\x";

        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.ReportAccess,
            new AccessReportRequest(мусор, "имя.trycloudflare.com", AccessMode.Tunnel));

        Assert.Null(registry.TryGetInfo(мусор));
    }

    [Fact]
    public async Task ReportAccess_по_незарегистрированной_СЗ_не_валит_коннект()
    {
        await using var conn = BuildConnection();
        await conn.StartAsync();

        await conn.InvokeAsync(HubRoutes.ReportAccess,
            new AccessReportRequest("999999", "имя.trycloudflare.com", AccessMode.Tunnel));

        Assert.Equal(HubConnectionState.Connected, conn.State);
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* временный файл */ }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { /* временная папка */ }
    }
}
