namespace SzDiag.Agent;

/// <summary>
/// Шаг набора тестов.
/// type = "command"    — PowerShell, ждём завершения, пишем stdout (Run).
/// type = "screenshot" — снимок экрана.
/// type = "app"        — запустить exe (Exe+Args) на DurationSeconds, снять скриншот
///                       под нагрузкой, убить дерево процессов (KillImage), опц.
///                       приложить лог результата (ResultFile). Для стресс-тестов
///                       (TM5, OCCT, Superposition, FurMark).
/// Пути Exe/ResultFile — относительные резолвятся рядом с exe агента.
/// </summary>
public sealed record TestStep(
    string Type,
    string Name,
    string? Run = null,
    string? Exe = null,
    string? Args = null,
    int? DurationSeconds = null,
    string? KillImage = null,
    string? ResultFile = null);
