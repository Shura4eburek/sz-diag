using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
        registry.Register("160705", "10.0.0.5", "PC-1", "conn-1");

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
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();
        registry.Register("160707", "10.0.0.7", "PC-3", "conn-3");

        await WithToken().PostAsJsonAsync(HubRoutes.AgentRevertStatusRoute,
            new RevertStatusReport("160707", false, "watchdog упал на System.IO"));

        var journal = File.ReadAllText(Path.Combine(_kbRoot, "СЗ", "160707", "журнал.md"));
        Assert.Contains("ОТКАТ НЕ ЗАВЕРШЁН", journal);
        Assert.Contains("System.IO", journal);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
