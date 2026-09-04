using System.IO.Compression;
using System.Text;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Разбор `occt-report.html` (бэклог п.124/#60, СЗ 161346): раньше это была ручная
/// распаковка gzip из HTML, а «10 минут вместо 180» читалось как «успешный прогон, ошибок 0».
///
/// Реальный формат JS-переменной <c>scheduleExecutionCompressed</c> в HTML не документирован
/// публично — тесты строят синтетический payload по структуре, которую видели при ручном
/// разборе (см. комментарий в issue #60): <c>periodExecutions[].testType/executedDuration/
/// errors/wheaErrors</c> + <c>elapsed</c> на верхнем уровне.</summary>
public class OcctReportParserTests
{
    private static string BuildHtml(string json, string quote = "\"")
    {
        var gzipped = Gzip(json);
        var b64 = Convert.ToBase64String(gzipped);
        return $"<html><script>var scheduleExecutionCompressed = {quote}{b64}{quote};</script></html>";
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    private const string TwoPeriodsJson = """
        {
          "periodExecutions": [
            { "testType": "Combined", "executedDuration": "00:05:00", "errors": 0, "wheaErrors": 0 },
            { "testType": "PowerSupply", "executedDuration": "00:05:00", "errors": 0, "wheaErrors": 0 }
          ],
          "elapsed": "00:10:20"
        }
        """;

    [Fact]
    public void TryParse_TwoCleanPeriods_ExtractsAll()
    {
        var summary = OcctReportParser.TryParse(BuildHtml(TwoPeriodsJson));

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.Periods.Count);
        Assert.Equal("Combined", summary.Periods[0].TestType);
        Assert.Equal(TimeSpan.FromMinutes(5), summary.Periods[0].ExecutedDuration);
        Assert.Equal(0, summary.TotalErrors);
        Assert.Equal(0, summary.TotalWheaErrors);
        Assert.Equal(TimeSpan.Parse("00:10:20"), summary.Elapsed);
    }

    [Fact]
    public void TryParse_ErrorsAndWheaErrors_Summed()
    {
        const string json = """
            { "periodExecutions": [
                { "testType": "Combined", "executedDuration": "00:30:00", "errors": 3, "wheaErrors": 1 },
                { "testType": "PowerSupply", "executedDuration": "00:30:00", "errors": 0, "wheaErrors": 0 }
              ], "elapsed": "01:00:00" }
            """;

        var summary = OcctReportParser.TryParse(BuildHtml(json));

        Assert.Equal(3, summary!.TotalErrors);
        Assert.Equal(1, summary.TotalWheaErrors);
    }

    [Fact]
    public void TryParse_NestedUnderSchedule_StillFound()
    {
        // Неизвестно заранее, лежит ли массив в корне или вложен — формат не документирован.
        const string json = """
            { "schedule": { "result": { "periodExecutions": [
                { "testType": "CpuOcct", "executedDuration": "00:45:00", "errors": 0, "wheaErrors": 0 }
              ] } }, "elapsed": "00:45:10" }
            """;

        var summary = OcctReportParser.TryParse(BuildHtml(json));

        Assert.NotNull(summary);
        Assert.Single(summary!.Periods);
        Assert.Equal("CpuOcct", summary.Periods[0].TestType);
    }

    [Fact]
    public void TryParse_SingleQuotedJsAssignment_StillFound()
    {
        var html = BuildHtml(TwoPeriodsJson, quote: "'");

        var summary = OcctReportParser.TryParse(html);

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.Periods.Count);
    }

    [Fact]
    public void TryParse_NoCompressedField_ReturnsNull()
    {
        Assert.Null(OcctReportParser.TryParse("<html><body>no data here</body></html>"));
    }

    [Fact]
    public void TryParse_EmptyOrNullHtml_ReturnsNull()
    {
        Assert.Null(OcctReportParser.TryParse(""));
        Assert.Null(OcctReportParser.TryParse(null!));
    }

    [Fact]
    public void TryParse_GarbageBase64_ReturnsNullNotThrows()
    {
        var html = "<script>var scheduleExecutionCompressed = \"not-valid-base64!!!\";</script>";
        Assert.Null(OcctReportParser.TryParse(html));
    }

    [Fact]
    public void TryParse_PlainJsonWithoutGzip_FallsBack()
    {
        // На случай, если формат когда-нибудь перестанет сжимать — не должны падать намертво.
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(TwoPeriodsJson));
        var html = $"<script>var scheduleExecutionCompressed = \"{b64}\";</script>";

        var summary = OcctReportParser.TryParse(html);

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.Periods.Count);
    }
}
