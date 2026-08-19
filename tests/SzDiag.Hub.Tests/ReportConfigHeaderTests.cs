using System.Text;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Метка конфигурации в шапке отчёта: отчёт собирает агент, а в какой конфигурации
/// шёл прогон, знает только хост. Без этой строки через неделю непонятно, профиль это был
/// или сток (СЗ 160697).</summary>
public class ReportConfigHeaderTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-rch-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-rch-{Guid.NewGuid():N}");

    public ReportConfigHeaderTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .WithoutSystemLogging());
    }

    private async Task UploadAsync(string sz, string timestamp, string fileName, string content)
    {
        var handler = _factory.Server.CreateHandler();
        await using var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "agents"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Headers[HubRoutes.TokenHeader] = "test-token";
                o.Transports = HttpTransportType.LongPolling;
            }).Build();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.UploadReportFile,
            new UploadReportPart(sz, timestamp, fileName, Encoding.UTF8.GetBytes(content)));
    }

    private string Saved(string sz, string timestamp, string fileName) =>
        File.ReadAllText(Path.Combine(_kbRoot, "СЗ", sz, "reports", timestamp, fileName));

    private ISessionStore Store => _factory.Services.GetRequiredService<ISessionStore>();

    [Fact]
    public async Task UploadReport_WithStoredConfig_InsertsLabelAfterTitle()
    {
        await Store.SetLastTestConfigAsync("160697", "EXPO 6000, штатний БЖ");

        await UploadAsync("160697", "20260810-170400", "report.md", "# Звіт 160697\n\nтіло\n");

        var saved = Saved("160697", "20260810-170400", "report.md");
        Assert.StartsWith("# Звіт 160697", saved);
        Assert.Contains("**Конфігурація прогону:** EXPO 6000, штатний БЖ", saved);
        Assert.Contains("тіло", saved);
    }

    [Fact]
    public async Task UploadReport_NonMarkdownFile_LeftUntouched()
    {
        await Store.SetLastTestConfigAsync("160715", "EXPO 6000, штатний БЖ");

        await UploadAsync("160715", "20260810-170400", "sensors.csv", "time,cpu\n");

        Assert.DoesNotContain("Конфігурація", Saved("160715", "20260810-170400", "sensors.csv"));
    }

    [Fact]
    public async Task UploadReport_WithoutStoredConfig_LeftUntouched()
    {
        await UploadAsync("160716", "20260810-170400", "report.md", "# Звіт 160716\n\nтіло\n");

        Assert.DoesNotContain("Конфігурація", Saved("160716", "20260810-170400", "report.md"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
