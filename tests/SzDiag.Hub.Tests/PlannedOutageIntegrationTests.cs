using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Плановое обесточивание сервиса не должно попадать в счётчик отказов ⚡ (бэклог
/// п.130, СЗ 161346): раньше рубильник на ночь давал новый `bootTime` и заводил обычный
/// вырубон наравне с настоящим дефектом.</summary>
public class PlannedOutageIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-po-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-po-{Guid.NewGuid():N}");

    public PlannedOutageIntegrationTests(WebApplicationFactory<Program> factory)
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

    private string JournalText(string sz) =>
        File.ReadAllText(Path.Combine(_kbRoot, "СЗ", sz, "журнал.md"));

    [Fact]
    public async Task Register_HardOffDuringMassOffline_ClassifiedAsPlannedNotFailure()
    {
        var boot1 = new DateTimeOffset(2026, 8, 12, 22, 0, 0, TimeSpan.Zero);
        var boot2 = new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);

        var registry = _factory.Services.GetRequiredService<SessionRegistry>();

        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("161346", "PC-1", boot1, ShutdownKind.HardOff));

        // Свет пропал у всех разом — сторож офлайна уже засёк массовую пропажу heartbeat.
        registry.RecordMassOfflineEvent();

        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("161346", "PC-1", boot2, ShutdownKind.HardOff));

        var text = JournalText("161346");
        Assert.Contains("планове знеструмлення", text);
        Assert.DoesNotContain("**вирубон**", text);

        var info = registry.GetActive().Single(s => s.Sz == "161346");
        Assert.Equal(0, info.RebootCount); // плановое обесточивание не идёт в счётчик ⚡
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
