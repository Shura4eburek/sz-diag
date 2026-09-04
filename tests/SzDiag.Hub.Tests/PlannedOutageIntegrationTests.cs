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

    private HubConnection BuildConnection() => BuildConnection(_factory);

    private static HubConnection BuildConnection(WebApplicationFactory<Program> factory)
    {
        var handler = factory.Server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "agents"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Headers[HubRoutes.TokenHeader] = "test-token";
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    /// <summary>Время, которое `SessionRegistry` видит внутри hub — подменяем DI-регистрацией
    /// поверх `_factory`, чтобы гонять сценарии «отказ днём, реконнект вечером» без реального
    /// ожидания (review W2 C-4: классификация должна идти по моменту ОТКАЗА, а не реконнекта).</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public void Set(DateTimeOffset at) => _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private WebApplicationFactory<Program> WithTimeAndHours(
        FakeTimeProvider time, string hoursStart, string hoursEnd)
        => _factory.WithWebHostBuilder(b => b
            .UseSetting("Hub:ServiceHoursStart", hoursStart)
            .UseSetting("Hub:ServiceHoursEnd", hoursEnd)
            .ConfigureServices(services => services.AddSingleton<TimeProvider>(time)));

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

    // review W2 C-4: раньше классификация шла по моменту РЕГИСТРАЦИИ (возврата), а не по
    // моменту отказа — настоящий дневной hard-off, обнаруженный агентом только вечером после
    // рабочих часов, ложно метился плановым обесточиванием.
    [Fact]
    public async Task Register_RealHardOffDuringWorkHours_ReconnectAfterHours_NotClassifiedAsPlanned()
    {
        var boot1 = new DateTimeOffset(2026, 8, 12, 6, 0, 0, TimeSpan.Zero);
        var boot2 = new DateTimeOffset(2026, 8, 12, 20, 30, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 12, 14, 0, 0, TimeSpan.Zero)); // день, в часах
        var factory = WithTimeAndHours(time, "08:00", "20:00");
        var registry = factory.Services.GetRequiredService<SessionRegistry>();

        await using var conn = BuildConnection(factory);
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("162001", "PC-1", boot1, ShutdownKind.HardOff));
        registry.Heartbeat("162001"); // последний живой heartbeat — ещё в рабочих часах (14:00)

        time.Set(new DateTimeOffset(2026, 8, 12, 20, 30, 0, TimeSpan.Zero)); // реконнект уже после часов
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("162001", "PC-1", boot2, ShutdownKind.HardOff));

        var text = JournalText("162001");
        Assert.Contains("**вирубон**", text);
        Assert.DoesNotContain("планове знеструмлення", text);

        var info = registry.GetActive().Single(s => s.Sz == "162001");
        Assert.Equal(1, info.RebootCount); // настоящий дефект обязан идти в счётчик ⚡
    }

    // review W2 C-4: ночной рубильник, вернувшийся утром внутри рабочих часов, не ловился —
    // `WasMassOfflineNear` сравнивал массовое событие (случившееся ночью) с моментом
    // РЕКОННЕКТА (утро), а не с моментом самого отказа.
    [Fact]
    public async Task Register_NightMassOutage_ReconnectsNextMorningWithinHours_ClassifiedAsPlanned()
    {
        var boot1 = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var boot2 = new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 12, 20, 0, 0, TimeSpan.Zero)); // вечер
        var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(services => services.AddSingleton<TimeProvider>(time)));
        var registry = factory.Services.GetRequiredService<SessionRegistry>();

        await using var conn = BuildConnection(factory);
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("162002", "PC-1", boot1, ShutdownKind.HardOff));

        time.Set(new DateTimeOffset(2026, 8, 12, 23, 0, 0, TimeSpan.Zero)); // момент реального отказа — ночь
        registry.Heartbeat("162002");
        registry.RecordMassOfflineEvent(); // сторож офлайна засёк массовую пропажу рядом с отказом

        time.Set(new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero)); // реконнект утром — 9 часов спустя
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("162002", "PC-1", boot2, ShutdownKind.HardOff));

        var text = JournalText("162002");
        Assert.Contains("планове знеструмлення", text);
        Assert.DoesNotContain("**вирубон**", text);

        var info = registry.GetActive().Single(s => s.Sz == "162002");
        Assert.Equal(0, info.RebootCount);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
