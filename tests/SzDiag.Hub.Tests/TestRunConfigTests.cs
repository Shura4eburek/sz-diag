using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Метка конфигурации у прогона обязательна. Без неё через неделю непонятно,
/// на профиле гнали или на стоке — ровно так на 160697 потерялись обе половины
/// дискриминатора «EXPO 6000 против стока».</summary>
public class TestRunConfigTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-cfg-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-cfg-{Guid.NewGuid():N}");

    public TestRunConfigTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:ManagementToken", "mgmt-token")
             .UseSetting("Hub:AgentToken", "agent-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .WithoutSystemLogging());
    }

    private HttpClient NewClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ManagementApi.TokenHeader, "mgmt-token");
        return client;
    }

    private ISessionStore Store => _factory.Services.GetRequiredService<ISessionStore>();

    private bool JournalExists(string sz) =>
        File.Exists(Path.Combine(_kbRoot, "СЗ", sz, "журнал.md"));

    [Fact]
    public async Task Test_WithoutConfig_ReturnsBadRequest_AndWritesNothing()
    {
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160697/test",
            new TestRunRequest("occt", null, false));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("--config", await res.Content.ReadAsStringAsync());
        Assert.False(JournalExists("160697"));
    }

    [Fact]
    public async Task Test_SameConfig_WithoutStoredLabel_ReturnsBadRequest()
    {
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160712/test",
            new TestRunRequest("occt", null, SameConfig: true));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Test_WithoutConfig_AfterStoredLabel_HintsSameConfig()
    {
        await Store.SetLastTestConfigAsync("160713", "EXPO 6000, штатний БЖ");

        var res = await NewClient().PostAsJsonAsync("/api/sessions/160713/test",
            new TestRunRequest("occt", null, false));

        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("EXPO 6000, штатний БЖ", body);
        Assert.Contains("--same-config", body);
    }

    [Fact]
    public async Task Test_WithConfig_ButNoSession_ReturnsNotFound_AndDoesNotRememberLabel()
    {
        // Прогон не стартовал — метку запоминать нельзя, иначе следующий `--same-config`
        // подставит конфигурацию, в которой ничего не гоняли.
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160714/test",
            new TestRunRequest("occt", "сток JEDEC 4800", false));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Null(await Store.GetLastTestConfigAsync("160714"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
