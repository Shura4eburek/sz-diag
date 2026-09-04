namespace SzDiag.Kb;

/// <summary>Отчёт прогона диагностики по СЗ.</summary>
/// <param name="WuFrozen">Заморожен ли Windows Update на клиенте в момент снятия отчёта
/// (маркер `szcli freeze`) — null, если проверка неприменима (обычный `test run`, не
/// read-only диагностика). На 160705/161716 заморозку не ставили неделями, и никто не
/// замечал: активную сессию без неё видно только в самом конце, на `close` (бэклог п.139).
/// false печатается прямо в шапке diag.md — «невозможно не заметить».</param>
public sealed record TestReport(
    string Sz,
    string Hostname,
    DateTimeOffset RunAt,
    IReadOnlyList<TestStepResult> Steps,
    bool? WuFrozen = null);
