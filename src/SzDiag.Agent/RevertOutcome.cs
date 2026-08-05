using System.Text;

namespace SzDiag.Agent;

/// <summary>Итог отката по шагам.
///
/// Боль (СЗ 160705, бэклог п.59): watchdog отработал, `agent.exe --revert` **упал на
/// необработанном `System.IO`-исключении**, и на клиентской машине навсегда остались учётка
/// `svc-diag`, портативный sshd под SYSTEM, правило фаервола и `LocalAccountTokenFilterPolicy`.
/// Задача при этом отстрелялась (`Next Run Time: N/A`) — второй попытки не будет, и ни hub,
/// ни CLI об этом ни слова. Это дыра ровно в том инварианте, который CLAUDE.md объявляет
/// ключевым: «доступ временный и откатывается без следов».
///
/// Поэтому шаги идут независимо: упавший логируется, но не прекращает откат остальных —
/// они и так идемпотентны, ради этого и писались.</summary>
public sealed record RevertOutcome(IReadOnlyList<string> Done, IReadOnlyList<RevertStepFailure> Failed)
{
    public bool AllClean => Failed.Count == 0;

    /// <summary>Человекочитаемая сводка для лога и вывода в консоль.</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append(AllClean
            ? $"Откат выполнен полностью ({Done.Count} шагов)."
            : $"Откат выполнен ЧАСТИЧНО: {Done.Count} шагов ок, {Failed.Count} с ошибкой.");
        foreach (var f in Failed)
            sb.Append($"\n  ✗ {f.Step}: {f.Error}");
        return sb.ToString();
    }
}

/// <param name="Step">Что откатывали (имя шага).</param>
/// <param name="Error">Полный текст исключения — на 160705 в Application-логе он был
/// обрезан, и тип с путём пришлось бы доставать из дампа.</param>
public sealed record RevertStepFailure(string Step, string Error);
