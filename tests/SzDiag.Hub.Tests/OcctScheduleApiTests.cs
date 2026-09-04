using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>`GET /api/occt/schedule` и `GET /api/sessions/{sz}/test-result` (бэклог п.124/#60,
/// СЗ 161346): план прогона печатается по тому, что РЕАЛЬНО лежит в раздаче
/// (<c>Hub.ToolsRoot/occt/…</c>), а не по репозиторию — раздача молча расходилась с репо
/// (5+5 минут против заявленных 90+90).</summary>
public class OcctScheduleApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-occtapi-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-occtapi-{Guid.NewGuid():N}");
    private readonly string _toolsRoot = Path.Combine(Path.GetTempPath(), $"sztools-occtapi-{Guid.NewGuid():N}");

    public OcctScheduleApiTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_toolsRoot, "occt"));
        File.WriteAllText(Path.Combine(_toolsRoot, "occt", "schedule.json"), """
            { "Periods": [ { "TestType": "Combined", "Duration": "00:30:00", "IsInfinite": false } ] }
            """);
        File.WriteAllText(Path.Combine(_toolsRoot, "occt", "schedule-long.json"), """
            { "Periods": [
                { "TestType": "Combined", "Duration": "01:30:00", "IsInfinite": false },
                { "TestType": "PowerSupply", "Duration": "01:30:00", "IsInfinite": false }
              ] }
            """);

        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:ManagementToken", "mgmt-token")
             .UseSetting("Hub:AgentToken", "agent-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .UseSetting("Hub:ToolsRoot", _toolsRoot)
             .WithoutSystemLogging());
    }

    private HttpClient Client()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(ManagementApi.TokenHeader, "mgmt-token");
        return c;
    }

    [Fact]
    public async Task Schedule_DefaultProfile_ReturnsDeployedPeriods()
    {
        var plan = await Client().GetFromJsonAsync<OcctSchedulePlan>("/api/occt/schedule");

        Assert.NotNull(plan);
        Assert.Single(plan!.Periods);
        Assert.Equal("Combined", plan.Periods[0].TestType);
        Assert.Equal(TimeSpan.FromMinutes(30), plan.TotalFinite);
    }

    [Fact]
    public async Task Schedule_LongProfile_SumsBothPeriods()
    {
        var plan = await Client().GetFromJsonAsync<OcctSchedulePlan>("/api/occt/schedule?profile=long");

        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Periods.Count);
        Assert.Equal(TimeSpan.FromHours(3), plan.TotalFinite);
    }

    [Fact]
    public async Task Schedule_UnknownProfile_BadRequest()
    {
        var resp = await Client().GetAsync("/api/occt/schedule?profile=ultra");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Schedule_ProfileNotDeployed_NotFound()
    {
        // smoke.json не создан в setup — раздача расходится с ожиданием.
        var resp = await Client().GetAsync("/api/occt/schedule?profile=smoke");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task TestResult_NoReportYet_NotFound()
    {
        var resp = await Client().GetAsync("/api/sessions/156864/test-result");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task TestResult_ReportOnDisk_ParsedAndReturned()
    {
        var reportsDir = Path.Combine(_kbRoot, "СЗ", "156864", "reports", "20260904-120000");
        Directory.CreateDirectory(reportsDir);
        var json = """
            { "periodExecutions": [
                { "testType": "Combined", "executedDuration": "00:05:00", "errors": 0, "wheaErrors": 0 }
              ], "elapsed": "00:05:10" }
            """;
        var gz = Gzip(json);
        var html = $"<script>var scheduleExecutionCompressed = \"{Convert.ToBase64String(gz)}\";</script>";
        File.WriteAllText(Path.Combine(reportsDir, "occt-report.html"), html);

        var summary = await Client().GetFromJsonAsync<OcctReportSummary>("/api/sessions/156864/test-result");

        Assert.NotNull(summary);
        Assert.Single(summary!.Periods);
        Assert.Equal("Combined", summary.Periods[0].TestType);
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, true); } catch { }
        try { if (Directory.Exists(_toolsRoot)) Directory.Delete(_toolsRoot, true); } catch { }
    }
}
