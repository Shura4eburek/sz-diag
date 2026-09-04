using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Итог `agent.exe --revert` вне SignalR-сессии (watchdog/headless-откат): без него
/// упавший на середине откат оставлял доступ на клиенте, а `szcli list` продолжал показывать
/// СЗ online (бэклог п.59, СЗ 160705).</summary>
public class RevertStatusApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-rvs-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-rvs-{Guid.NewGuid():N}");

    public RevertStatusApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .WithoutSystemLogging());
    }

    private HttpClient WithToken()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(HubRoutes.TokenHeader, "test-token");
        return c;
    }

    [Fact]
    public async Task Failure_MarksSessionAsProblemNotOnline()
    {
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();
        registry.Register("160705", "127.0.0.1", "PC-1", "conn-1");

        var r = await WithToken().PostAsJsonAsync(HubRoutes.AgentRevertStatusRoute,
            new RevertStatusReport("160705", false, "sshd не снят: Access denied"));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var info = registry.GetActive().Single(s => s.Sz == "160705");
        Assert.Equal(SessionStatus.Offline, info.Status);
        Assert.Contains("Access denied", info.RevertNote);
    }

    [Fact]
    public async Task Success_RemovesSessionFromRegistry()
    {
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();
        registry.Register("160706", "10.0.0.6", "PC-2", "conn-2");

        var r = await WithToken().PostAsJsonAsync(HubRoutes.AgentRevertStatusRoute,
            new RevertStatusReport("160706", true, "откат выполнен чисто"));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotContain(registry.GetActive(), s => s.Sz == "160706");
    }

    [Fact]
    public async Task NoToken_Unauthorized()
    {
        var r = await _factory.CreateClient().PostAsJsonAsync(HubRoutes.AgentRevertStatusRoute,
            new RevertStatusReport("160705", false, "x"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Failure_WritesJournalEntry()
    {
        // Журнал СЗ — на украинском, как весь kb (Important-4, ревью волны 1): раньше этот
        // путь единственный писал по-русски.
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();
        registry.Register("160707", "10.0.0.7", "PC-3", "conn-3");

        await WithToken().PostAsJsonAsync(HubRoutes.AgentRevertStatusRoute,
            new RevertStatusReport("160707", false, "watchdog упал на System.IO"));

        var journal = File.ReadAllText(Path.Combine(_kbRoot, "СЗ", "160707", "журнал.md"));
        Assert.Contains("ВІДКАТ НЕ ЗАВЕРШЕНО", journal);
        Assert.Contains("System.IO", journal);
    }

    [Fact]
    public async Task InvalidSz_RejectsBeforeTouchingRegistryOrJournal()
    {
        // Critical-5 (ревью волны 1): токен /agent/* общий на всех агентов, поэтому Sz из
        // тела нельзя принимать как есть — без валидации формата произвольная строка уезжала
        // прямо в KbPaths.SzDir без санитизации (запись мимо vault).
        var r = await WithToken().PostAsJsonAsync(HubRoutes.AgentRevertStatusRoute,
            new RevertStatusReport(@"..\..\..\Users\Public\x", false, "x"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_kbRoot, "СЗ")));
    }

    [Fact]
    public async Task Success_RecordsCloseInHistory()
    {
        // Important-3 (ревью волны 1): watchdog/headless-откат уходит без живого SignalR-
        // коннекта, поэтому обычный SessionCloser его не увидит — без явной записи здесь
        // чистый откат по этому пути не оставлял в SQLite закрытия заявки.
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();
        var store = _factory.Services.GetRequiredService<ISessionStore>();
        var opened = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.RecordOpenAsync(new SessionRecord("160708", "10.0.0.8", "PC-4", opened, null));
        registry.Register("160708", "10.0.0.8", "PC-4", "conn-4");

        var r = await WithToken().PostAsJsonAsync(HubRoutes.AgentRevertStatusRoute,
            new RevertStatusReport("160708", true, "відкат виконано повністю"));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var history = await store.GetHistoryAsync();
        var record = history.Single(h => h.Sz == "160708");
        Assert.NotNull(record.ClosedAt);
    }

    [Fact]
    public void IsAuthorizedForSz_MismatchedIp_IsRejected()
    {
        // Critical-5, вторая половина: даже с валидным форматом Sz заражённый клиент не
        // должен иметь возможности отчитаться за чужую активную СЗ и выкинуть её из реестра.
        var registry = new SessionRegistry();
        registry.Register("160709", "10.0.0.9", "PC-5", "conn-5");

        Assert.False(RevertStatusApi.IsAuthorizedForSz(registry, "160709", "6.6.6.6"));
        Assert.True(RevertStatusApi.IsAuthorizedForSz(registry, "160709", "10.0.0.9"));
    }

    [Fact]
    public void IsAuthorizedForSz_UnknownSessionOrUnknownIp_IsPermissive()
    {
        // Сверять не с чем: сессии уже нет (обычный случай — watchdog шлёт репорт как раз
        // потому, что живого коннекта больше нет) или IP вызова не определился (хостинг без
        // реального сокета) — не блокируем то, что и так нечем проверить.
        var registry = new SessionRegistry();
        registry.Register("160710", "10.0.0.10", "PC-6", "conn-6");

        Assert.True(RevertStatusApi.IsAuthorizedForSz(registry, "000000", "6.6.6.6"));
        Assert.True(RevertStatusApi.IsAuthorizedForSz(registry, "160710", null));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
