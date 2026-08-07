using System.Diagnostics;

namespace SzDiag.Agent;

/// <summary>Чем машина занята **прямо сейчас** — по живым процессам, а не по последней
/// выполненной команде.
///
/// Боль (бэклог п.73, СЗ 160306): после `diag` активность оставалась
/// `готов · диагностика: storage, reboots, whea, livekernel` и висела так двадцать минут,
/// включая весь стресс-прогон OCCT. Колонка «Была занята» существует ровно для ответа
/// «под чем машина вырубилась» — и дала бы неверный: «на диагностике», хотя реальный
/// кандидат — стресс-нагрузка.</summary>
public static class ActivityProbe
{
    /// <summary>Процессы стресс-тулов (без .exe). Совпадает со списком наблюдателя сенсоров:
    /// «нагрузка идёт» подтверждается живым процессом, а не намерением.</summary>
    public static readonly string[] StressProcesses =
        { "OCCT", "OCCTCmd", "OCCTEnterprise", "TM5", "FurMark", "furmark", "prime95", "3DMark", "lhmmon" };

    /// <summary>Строка активности по текущему состоянию машины. `idleLabel` — что писать,
    /// когда ничего не идёт (у resume-ветки своя подпись).</summary>
    public static string Describe(IReadOnlyList<string> runningStress, int backgroundJobs,
        string idleLabel = "— готов")
    {
        var parts = new List<string>();
        if (runningStress.Count > 0)
            parts.Add("стресс: " + string.Join(", ", runningStress.Distinct(StringComparer.OrdinalIgnoreCase)));
        if (backgroundJobs > 0)
            parts.Add($"фоновых задач: {backgroundJobs}");

        return parts.Count == 0 ? idleLabel : string.Join(" · ", parts);
    }

    /// <summary>Живые процессы стресс-тулов. Через <see cref="Process.GetProcessesByName"/>,
    /// а не PowerShell: опрос идёт в heartbeat-цикле и обязан быть дешёвым — под 100 %
    /// нагрузкой запуск powershell.exe сам становится узким местом (п.64).</summary>
    public static IReadOnlyList<string> RunningStress()
    {
        var found = new List<string>();
        foreach (var name in StressProcesses)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0) found.Add(name);
                foreach (var p in procs) p.Dispose();
            }
            catch { /* процесс мог умереть между вызовами — не повод ронять heartbeat */ }
        }
        return found;
    }
}
