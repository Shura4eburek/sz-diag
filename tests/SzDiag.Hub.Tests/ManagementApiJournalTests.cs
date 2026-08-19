using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Журнал СЗ на стороне API: ручные заметки и следы команд. Проверяем по файлу
/// в тестовом vault, а не по моку — так тест заодно ловит формат записи.</summary>
public class ManagementApiJournalTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-jrn-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-jrn-{Guid.NewGuid():N}");

    public ManagementApiJournalTests(WebApplicationFactory<Program> factory)
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

    private string JournalPath(string sz) => Path.Combine(_kbRoot, "СЗ", sz, "журнал.md");
    private string JournalText(string sz) => File.ReadAllText(JournalPath(sz));
    private bool JournalExists(string sz) => File.Exists(JournalPath(sz));

    [Fact]
    public async Task Journal_ValidNote_ReturnsOk_AndWritesManualEntry()
    {
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160697/journal",
            new JournalNoteRequest("поставив тестовий Corsair RM850x"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("✋ поставив тестовий Corsair RM850x", JournalText("160697"));
    }

    [Fact]
    public async Task Journal_EmptyText_ReturnsBadRequest()
    {
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160698/journal",
            new JournalNoteRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(JournalExists("160698"));
    }

    [Fact]
    public async Task Journal_BadSzNumber_ReturnsBadRequest_AndCreatesNothing()
    {
        var res = await NewClient().PostAsJsonAsync("/api/sessions/--help/journal",
            new JournalNoteRequest("текст"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_kbRoot, "СЗ", "--help")));
    }

    [Fact]
    public async Task Journal_NoActiveSession_StillAccepted()
    {
        // Ни одного агента не регистрировали: заметка мастера должна приниматься всё равно —
        // он отходит от машины, а зафиксировать физический шаг надо в момент, когда он сделан.
        var res = await NewClient().PostAsJsonAsync("/api/sessions/160699/journal",
            new JournalNoteRequest("майстер вимкнув EXPO в BIOS"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("майстер вимкнув EXPO в BIOS", JournalText("160699"));
    }

    [Fact]
    public async Task Diag_WhenSessionMissing_WritesNothing()
    {
        // Команда не выполнилась — врать про неё в журнале нельзя.
        var res = await NewClient().PostAsync("/api/sessions/160701/diag", null);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.False(JournalExists("160701"));
    }

    [Fact]
    public async Task Close_WhenSessionMissing_WritesNothing()
    {
        var res = await NewClient().PostAsync("/api/sessions/160702/close", null);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.False(JournalExists("160702"));
    }

    public void Dispose()
    {
        // Пул соединений SQLite держит файл: без сброса удаление падает (как в ManagementApiTests).
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
