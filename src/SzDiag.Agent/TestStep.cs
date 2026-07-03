namespace SzDiag.Agent;

/// <summary>
/// Шаг набора тестов.
/// type = "command"    — PowerShell, ждём завершения, пишем stdout (Run).
/// type = "screenshot" — снимок экрана.
/// type = "app"        — запустить exe (Exe+Args), снять скриншот и убить дерево
///                       процессов (KillImage). Два режима:
///                       • стресс (по умолчанию): держим нагрузку DurationSeconds,
///                         скриншот под нагрузкой, затем kill (TM5, FurMark, 3DMark);
///                       • RunToCompletion=true: ждём самозавершения процесса до
///                         DurationSeconds (таймаут-предохранитель), скриншот в
///                         процессе; ранний выход — норма, не ошибка (OCCT: гоняет
///                         расписание и сам закрывается, отдав отчёт).
///                       • RunToCompletion=true + CompletionWindowClass: тул сам не
///                         закрывается (TM5 виснет на модалке "тест завершён") — вместо
///                         самозавершения ждём появления окна с этим Win32-классом
///                         (напр. "#32770" — стандартный MessageBox) у процесса,
///                         снимаем скриншот (с самой модалкой) и всё равно убиваем
///                         дерево — не дожидаясь DurationSeconds.
///                       Опц. ResultFile — небольшой текст-лог, встраивается в отчёт;
///                       ArtifactFile — файл-артефакт (напр. HTML-отчёт OCCT), не
///                       встраивается, а прикладывается к отчёту ссылкой и заливается
///                       на hub рядом со скриншотами.
/// В Args доступна подстановка {workdir} → абсолютный каталог exe (для путей к
/// schedule/report, которые тул резолвит по-своему).
/// Пути Exe/ResultFile/ArtifactFile — относительные резолвятся рядом с exe агента.
/// </summary>
public sealed record TestStep(
    string Type,
    string Name,
    string? Run = null,
    string? Exe = null,
    string? Args = null,
    int? DurationSeconds = null,
    string? KillImage = null,
    string? ResultFile = null,
    bool RunToCompletion = false,
    string? ArtifactFile = null,
    string? CompletionWindowClass = null,
    string? Id = null);
