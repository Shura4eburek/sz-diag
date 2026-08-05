using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using SzDiag.Contracts;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Hub.Tests;

public class ReportUploadIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-rep-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-rep-{Guid.NewGuid():N}");

    public ReportUploadIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .WithoutSystemLogging());
    }

    [Fact]
    public async Task UploadReportFile_WritesIntoKb()
    {
        var handler = _factory.Server.CreateHandler();
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "agents"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Headers[HubRoutes.TokenHeader] = "test-token";
                o.Transports = HttpTransportType.LongPolling;
            }).Build();
        await conn.StartAsync();

        await conn.InvokeAsync(HubRoutes.UploadReportFile,
            new UploadReportPart("156864", "20260701-120000", "report.md", "hello"u8.ToArray()));

        var path = Path.Combine(new KbPaths(_kbRoot).ReportDir("156864", "20260701-120000"), "report.md");
        Assert.True(File.Exists(path));
        Assert.Equal("hello", File.ReadAllText(path));

        await conn.DisposeAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
