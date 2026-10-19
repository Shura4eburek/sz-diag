using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Адресация агента после реконнекта (СЗ 162003, 15.09.2026, бэклог п.273).
///
/// Боль: `ConnectionId` писался в реестр только в `Register`, а `Register` агент зовёт один
/// раз за жизнь процесса. Соединение построено с `WithAutomaticReconnect()`, и после любого
/// обрыва (через Cloudflare Tunnel это штатное событие) SignalR выдаёт НОВЫЙ ConnectionId.
/// Heartbeat с нового соединения поднимал сессию в `online`, а все команды hub→агент
/// (`Exec`, `Push`, `Pull`, `RestartAgent`, `Revert`, `close`!) уходили на старый, закрытый
/// id — `Clients.Client(мёртвый)` в SignalR тихий no-op, поэтому CLI видел только таймаут
/// «агент не ответил», а агент в своём логе не видел ни одной команды.
///
/// Здесь фиксируем инвариант: адрес агента в реестре — это соединение, с которого пришёл
/// ПОСЛЕДНИЙ heartbeat.</summary>
public class AgentHubReconnectAddressingTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-ara-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-ara-{Guid.NewGuid():N}");

    public AgentHubReconnectAddressingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:ManagementToken", "mgmt-token")
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
    public async Task Heartbeat_FromNewConnection_RepointsAgentAddress()
    {
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();

        await using var first = BuildConnection();
        await first.StartAsync();
        await first.InvokeAsync(HubRoutes.Register, new RegisterRequest("162003", "PC-1"));
        var firstId = registry.TryGetConnectionId("162003");
        Assert.Equal(first.ConnectionId, firstId);

        // Реконнект: агент пришёл новым соединением и, как в реальности, Register больше
        // не зовёт — только heartbeat.
        await using var second = BuildConnection();
        await second.StartAsync();
        await second.InvokeAsync(HubRoutes.Heartbeat, "162003");

        Assert.Equal(second.ConnectionId, registry.TryGetConnectionId("162003"));
        Assert.NotEqual(firstId, registry.TryGetConnectionId("162003"));
    }

    [Fact]
    public async Task Heartbeat_AfterOldConnectionClosed_KeepsSessionAddressable()
    {
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();

        var first = BuildConnection();
        await first.StartAsync();
        await first.InvokeAsync(HubRoutes.Register, new RegisterRequest("162004", "PC-2"));

        await using var second = BuildConnection();
        await second.StartAsync();
        await second.InvokeAsync(HubRoutes.Heartbeat, "162004");

        // Старое соединение закрывается ПОСЛЕ того, как агент уже переехал на новое:
        // именно так выглядит реконнект со стороны hub. OnDisconnected по старому id не
        // должен ни обнулять адрес, ни ронять сессию в offline.
        await first.StopAsync();
        await first.DisposeAsync();
        await second.InvokeAsync(HubRoutes.Heartbeat, "162004");

        Assert.Equal(second.ConnectionId, registry.TryGetConnectionId("162004"));
        Assert.Equal(SessionStatus.Online, registry.GetActive().Single(s => s.Sz == "162004").Status);
    }

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, true); } catch { }
    }
}
