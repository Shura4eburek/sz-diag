using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>События машины в журнале СЗ: вырубон должен оставлять строку сам, без команды
/// с хоста. На 160697 ход диагностики восстанавливать было не по чему — сессия чата кончилась,
/// а в kb осталась только последняя ручная правка.</summary>
public class AgentHubJournalTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-ajr-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-ajr-{Guid.NewGuid():N}");

    public AgentHubJournalTests(WebApplicationFactory<Program> factory)
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

    private string JournalText(string sz) =>
        File.ReadAllText(Path.Combine(_kbRoot, "СЗ", sz, "журнал.md"));

    [Fact]
    public async Task Register_WhenBootTimeChanged_WritesMachineEntryWithUptime()
    {
        var boot1 = new DateTimeOffset(2026, 8, 10, 17, 1, 0, TimeSpan.Zero);
        var boot2 = new DateTimeOffset(2026, 8, 10, 17, 22, 0, TimeSpan.Zero);   // продержалась 21 минуту

        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register,
            new RegisterRequest("160697", "PC-1", boot1, ShutdownKind.HardOff));
        await conn.InvokeAsync(HubRoutes.ReportActivity, "160697", "OCCT Combined", DateTimeOffset.UtcNow);
        await conn.InvokeAsync(HubRoutes.Register,
            new RegisterRequest("160697", "PC-1", boot2, ShutdownKind.HardOff));

        var text = JournalText("160697");
        Assert.Contains("⚡", text);
        Assert.Contains("вирубон", text);
        Assert.Contains("00:21", text);
        Assert.Contains("OCCT Combined", text);
    }

    [Fact]
    public async Task Register_ButtonShutdown_WritesRebootNotFailure()
    {
        // Кнопкой выключил мастер после осмотра — в отказы это не идёт (бэклог п.93).
        var boot1 = new DateTimeOffset(2026, 8, 10, 16, 30, 0, TimeSpan.Zero);
        var boot2 = new DateTimeOffset(2026, 8, 10, 17, 1, 44, TimeSpan.Zero);

        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register,
            new RegisterRequest("160710", "PC-2", boot1, ShutdownKind.PowerButton));
        await conn.InvokeAsync(HubRoutes.Register,
            new RegisterRequest("160710", "PC-2", boot2, ShutdownKind.PowerButton));

        var text = JournalText("160710");
        Assert.Contains("перезавантаження", text);
        Assert.DoesNotContain("вирубон", text);
    }

    [Fact]
    public async Task Register_FirstConnect_WritesNoRebootEntry()
    {
        var boot = new DateTimeOffset(2026, 8, 10, 17, 1, 0, TimeSpan.Zero);

        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register,
            new RegisterRequest("160711", "PC-3", boot, ShutdownKind.HardOff));

        var path = Path.Combine(_kbRoot, "СЗ", "160711", "журнал.md");
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        Assert.DoesNotContain("вирубон", text);
    }

    [Fact]
    public async Task PowerEvents_SleepEvent_WritesJournalEntryWithDuration()
    {
        // Бэклог п.140/222 (СЗ 161346): сессия пережила незапланированный сон, а ни hub, ни
        // агент такого события не фиксировали — разбирать пришлось задним числом по журналу.
        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("161346", "PC-4"));

        var sleepStart = new DateTimeOffset(2026, 8, 24, 14, 44, 14, TimeSpan.Zero);
        await conn.InvokeAsync(HubRoutes.PowerEvents, new PowerEventsReport("161346", new[]
        {
            new PowerEvent(sleepStart, ShutdownKind.Sleep, DurationSeconds: 540), // 9 минут
        }));

        var text = JournalText("161346");
        Assert.Contains("сон", text);
    }

    [Fact]
    public async Task PowerEvents_SleepEventTwice_WritesJournalEntryOnlyOnce()
    {
        // Агент присылает журнал при каждом переподключении — уже влитое событие не должно
        // дублироваться в журнале СЗ.
        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("161347", "PC-5"));

        var report = new PowerEventsReport("161347", new[]
        {
            new PowerEvent(new DateTimeOffset(2026, 8, 24, 14, 44, 14, TimeSpan.Zero),
                ShutdownKind.Sleep, DurationSeconds: 540),
        });
        await conn.InvokeAsync(HubRoutes.PowerEvents, report);
        await conn.InvokeAsync(HubRoutes.PowerEvents, report);

        var text = JournalText("161347");
        var occurrences = text.Split("сон").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task Register_ReconnectAfterHeartbeatGap_WritesJournalEntry()
    {
        // Отвал под фоновой задачей без реального ребута — тоже факт, который иначе всплывает
        // только по памяти инженера (бэклог п.202, СЗ 161972).
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();
        var boot = new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);

        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("161972", "PC-6", boot));
        await conn.InvokeAsync(HubRoutes.ReportActivity, "161972", "Disk linear scan", DateTimeOffset.UtcNow);

        registry.MarkStaleOffline(TimeSpan.Zero); // симулируем пропажу heartbeat прямо сейчас
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("161972", "PC-6", boot)); // тот же boot-time

        var text = JournalText("161972");
        Assert.Contains("з'єднання відновлено", text);
        Assert.Contains("Disk linear scan", text);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
