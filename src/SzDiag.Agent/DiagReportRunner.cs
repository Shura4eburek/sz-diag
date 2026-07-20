using System.Text;
using SzDiag.Contracts;
using SzDiag.Kb;

namespace SzDiag.Agent;

/// <summary>Исход диагностики: гоняли ли, короткая метка, все доступные секции.</summary>
public sealed record DiagRunOutcome(bool Ran, string RanLabel, IReadOnlyList<string> AvailableSections);

/// <summary>
/// Оркестрация read-only диагностики: отбор секций по фильтру → прогон через TestRunner →
/// diag.md (DiagReportBuilder) → заливка на hub. Read-only: без скриншотов/артефактов/стресса.
/// Переиспользует FilterSteps/AvailableIds из TestReportRunner (та же семантика фильтра по id).
/// </summary>
public sealed class DiagReportRunner
{
    private readonly TestRunner _runner;
    private readonly TestSuite _suite;
    private readonly IHubLink _link;
    private readonly string _hostname;
    private readonly Func<DateTimeOffset> _now;

    public DiagReportRunner(TestRunner runner, TestSuite suite, IHubLink link,
        string hostname, Func<DateTimeOffset>? now = null)
    {
        _runner = runner;
        _suite = suite;
        _link = link;
        _hostname = hostname;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public async Task<DiagRunOutcome> RunAndUploadAsync(string sz, string? sections = null, CancellationToken ct = default)
    {
        var steps = TestReportRunner.FilterSteps(_suite.Steps, sections);
        if (steps.Count == 0)
            return new DiagRunOutcome(false, "", TestReportRunner.AvailableIds(_suite.Steps));

        var now = _now();
        var timestamp = now.ToString("yyyyMMdd-HHmmss");
        var runSuite = new TestSuite { Steps = steps };

        void OnStep(TestStep s)
        {
            try { _ = _link.ReportActivityAsync(sz, $"диагностика: {s.Name}", DateTimeOffset.UtcNow, ct); }
            catch { /* статус не критичен */ }
        }

        // CIM-пробы быстрые, но события/reliability могут занять секунды — уводим с потока SignalR.
        var output = await Task.Run(() => _runner.Run(runSuite, sz, _hostname, now, OnStep), ct);

        var md = DiagReportBuilder.Build(output.Report);
        await _link.UploadReportFileAsync(
            new UploadReportPart(sz, timestamp, "diag.md", Encoding.UTF8.GetBytes(md)), ct);

        var ranLabel = string.IsNullOrWhiteSpace(sections)
            ? "полная диагностика"
            : string.Join(", ", TestReportRunner.AvailableIds(steps));
        return new DiagRunOutcome(true, ranLabel, TestReportRunner.AvailableIds(_suite.Steps));
    }
}
