using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>`/healthz` — минимальный эндпоинт со счётчиками ThreadPool, который обязан
/// отвечать даже когда остальной hub захлебнулся (бэклог п.50, СЗ 160306): 3674 потока и
/// 12605 хендлов на живой заявке нечем было отличить от «hub умер», кроме подсчёта потоков
/// руками через Get-Process. Эндпоинт живёт вне `/api` (без mgmt-токена) и вне `/agents`
/// (без agent-токена) — иначе он сам может упереться в те же залипшие проверки.</summary>
public class HealthApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-health-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-health-{Guid.NewGuid():N}");

    public HealthApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .WithoutSystemLogging());
    }

    [Fact]
    public async Task Healthz_RespondsWithoutAnyToken()
    {
        var client = _factory.CreateClient();

        var r = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Healthz_ReportsThreadPoolCounters()
    {
        var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<HealthzResponse>("/healthz");

        Assert.NotNull(body);
        Assert.True(body!.ThreadCount > 0);
        Assert.True(body.AvailableWorkerThreads >= 0);
        Assert.True(body.AvailableCompletionPortThreads >= 0);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, true); } catch { }
    }
}
