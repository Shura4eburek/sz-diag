using System.Text;
using SzDiag.Contracts;
using SzDiag.Kb;

namespace SzDiag.Agent;

/// <summary>Исход прогона для агента: гоняли ли что-то, всё ли чисто, короткая метка и доступные id.</summary>
public sealed record TestRunOutcome(bool Ran, bool AllClean, string RanLabel, IReadOnlyList<string> AvailableIds);

/// <summary>Оркестрация: фильтр шагов → прогон → report.md → загрузка файлов на hub + пуш активности.</summary>
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

    /// <summary>Отобрать шаги по фильтру (список id через запятую). Пусто/null — весь набор.</summary>
    public static IReadOnlyList<TestStep> FilterSteps(IReadOnlyList<TestStep> steps, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return steps;
        var ids = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).ToHashSet();
        return steps.Where(s => s.Id is not null && ids.Contains(s.Id.ToLowerInvariant())).ToList();
    }

    /// <summary>Id всех шагов, у которых он задан (для подсказки при опечатке в фильтре).</summary>
    public static IReadOnlyList<string> AvailableIds(IReadOnlyList<TestStep> steps)
        => steps.Where(s => !string.IsNullOrWhiteSpace(s.Id)).Select(s => s.Id!).ToList();

    public async Task<TestRunOutcome> RunAndUploadAsync(string sz, string? filter = null, CancellationToken ct = default)
    {
        var steps = FilterSteps(_suite.Steps, filter);
        if (steps.Count == 0)
            return new TestRunOutcome(false, true, "", AvailableIds(_suite.Steps));

        var now = _now();
        var timestamp = now.ToString("yyyyMMdd-HHmmss");
        var runSuite = new TestSuite { Steps = steps };

        // Пуш активности на старте каждого шага; время старта — реальное UtcNow.
        void OnStep(TestStep s)
        {
            var label = s.Type.Equals("app", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.Id)
                ? $"Тест {s.Id!.ToUpperInvariant()}"
                : s.Name;
            try { _ = _link.ReportActivityAsync(sz, label, DateTimeOffset.UtcNow, ct); }
            catch { /* статус не критичен */ }
        }

        // Стресс-тесты держат нагрузку минутами — уводим с потока обработчика SignalR.
        var output = await Task.Run(() => _runner.Run(runSuite, sz, _hostname, now, OnStep), ct);

        var md = ReportMarkdownBuilder.Build(output.Report);
        await _link.UploadReportFileAsync(
            new UploadReportPart(sz, timestamp, "report.md", Encoding.UTF8.GetBytes(md)), ct);

        foreach (var (fileName, bytes) in output.Screenshots)
            await _link.UploadReportFileAsync(new UploadReportPart(sz, timestamp, fileName, bytes), ct);

        foreach (var (fileName, bytes) in output.Artifacts)
            await _link.UploadReportFileAsync(new UploadReportPart(sz, timestamp, fileName, bytes), ct);

        var allClean = output.Report.Steps.All(s =>
            s.Error is null && (s.Output is null || !s.Output.Contains('⚠')));
        var ranLabel = string.IsNullOrWhiteSpace(filter)
            ? "полный прогон"
            : string.Join(", ", AvailableIds(steps).Select(i => i.ToUpperInvariant()));
        if (string.IsNullOrEmpty(ranLabel)) ranLabel = "прогон";

        return new TestRunOutcome(true, allClean, ranLabel, AvailableIds(_suite.Steps));
    }
}
