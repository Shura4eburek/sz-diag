using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Сквозная проверка журнала вырубонов: агент регистрируется с новым boot-time
/// (то есть машина перезагрузилась), hub фиксирует событие в SQLite, CLI видит его через
/// `/api/sessions/{sz}/reboots`. Ровно тот путь, которого не хватило, когда вырубон на нашем
/// же стенде обнаружили через неделю (бэклог п.55).</summary>
public class RebootEndToEndTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-rb-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-rb-{Guid.NewGuid():N}");

    public RebootEndToEndTests(WebApplicationFactory<Program> factory)
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

    private HttpClient Cli()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(ManagementApi.TokenHeader, "mgmt-token");
        return c;
    }

    [Fact]
    public async Task NewBootTime_LandsInTimelineWithUptimeAndActivity()
    {
        var boot1 = new DateTimeOffset(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);
        var boot2 = new DateTimeOffset(2026, 7, 30, 16, 15, 5, TimeSpan.Zero);   // 53 часа спустя

        var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("160306", "PC-1", boot1));
        await conn.InvokeAsync(HubRoutes.ReportActivity, "160306", "OCCT Combined", DateTimeOffset.UtcNow);

        // Машина вырубилась и поднялась заново — агент регистрируется с новым boot-time.
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("160306", "PC-1", boot2));

        var timeline = await Cli().GetFromJsonAsync<RebootTimeline>("/api/sessions/160306/reboots");

        var evt = Assert.Single(timeline!.Events);
        Assert.Equal(boot1, evt.PreviousBootTime);
        Assert.Equal(boot2, evt.NewBootTime);
        Assert.Equal((long)(boot2 - boot1).TotalSeconds, evt.UptimeBeforeSeconds);
        Assert.Equal("OCCT Combined", evt.ActivityBefore);
        Assert.Equal(timeline.MaxUptimeSeconds, evt.UptimeBeforeSeconds);

        // И это видно в списке СЗ без отдельного запроса.
        var sessions = await Cli().GetFromJsonAsync<List<SessionInfo>>("/api/sessions");
        Assert.Equal(1, sessions!.Single(s => s.Sz == "160306").RebootCount);

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_WithSameBootTime_LeavesTimelineEmpty()
    {
        // Под нагрузкой heartbeat лагает и SignalR переподключается — вырубоном это не является.
        var boot = new DateTimeOffset(2026, 8, 4, 13, 3, 16, TimeSpan.Zero);

        var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("160636", "PC-2", boot));
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("160636", "PC-2", boot));

        var timeline = await Cli().GetFromJsonAsync<RebootTimeline>("/api/sessions/160636/reboots");

        Assert.Empty(timeline!.Events);
        await conn.DisposeAsync();
    }

    [Fact]
    public async Task UnknownSz_ReturnsEmptyTimelineNot404()
    {
        // CLI печатает «вырубонов не зафиксировано» — это нормальный ответ, а не ошибка.
        var timeline = await Cli().GetFromJsonAsync<RebootTimeline>("/api/sessions/111222/reboots");

        Assert.NotNull(timeline);
        Assert.Empty(timeline!.Events);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
