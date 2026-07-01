using System.Text;
using SzDiag.Contracts;
using SzDiag.Kb;

namespace SzDiag.Agent;

/// <summary>Оркестрация: прогон набора → report.md → загрузка файлов на hub.</summary>
public sealed class TestReportRunner
{
    private readonly TestRunner _runner;
    private readonly TestSuite _suite;
    private readonly IHubLink _link;
    private readonly string _hostname;
    private readonly Func<DateTimeOffset> _now;

    public TestReportRunner(TestRunner runner, TestSuite suite, IHubLink link,
        string hostname, Func<DateTimeOffset>? now = null)
    {
        _runner = runner;
        _suite = suite;
        _link = link;
        _hostname = hostname;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public async Task RunAndUploadAsync(string sz, CancellationToken ct = default)
    {
        var now = _now();
        var timestamp = now.ToString("yyyyMMdd-HHmmss");
        var output = _runner.Run(_suite, sz, _hostname, now);

        var md = ReportMarkdownBuilder.Build(output.Report);
        await _link.UploadReportFileAsync(
            new UploadReportPart(sz, timestamp, "report.md", Encoding.UTF8.GetBytes(md)), ct);

        foreach (var (fileName, bytes) in output.Screenshots)
            await _link.UploadReportFileAsync(new UploadReportPart(sz, timestamp, fileName, bytes), ct);
    }
}
