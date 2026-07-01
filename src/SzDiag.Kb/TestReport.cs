namespace SzDiag.Kb;

/// <summary>Отчёт прогона диагностики по СЗ.</summary>
public sealed record TestReport(
    string Sz,
    string Hostname,
    DateTimeOffset RunAt,
    IReadOnlyList<TestStepResult> Steps);
