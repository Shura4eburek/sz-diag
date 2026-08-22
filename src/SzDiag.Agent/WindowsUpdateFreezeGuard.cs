using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Переприменение заморозки Windows Update после ребута.
///
/// Боль (бэклог п.72, СЗ 160306): до перезагрузки все три службы стояли `Start=4`, после неё
/// `wuauserv` поднялся сам — `Start=3 Running`. Ребут — штатная часть долгой диагностики, и
/// размороженный WU в этот момент приносит обновление и ещё один ребут, который потом
/// читается как вырубон железа.
///
/// Агент при старте (в том числе headless `--resume`) видит маркер, оставленный `szcli freeze`,
/// и применяет заморозку заново. Скрипт идемпотентен, поэтому повтор безопасен.</summary>
public static class WindowsUpdateFreezeGuard
{
    /// <summary>Есть ли на машине незакрытая заморозка.</summary>
    public static bool IsMarked() => File.Exists(WindowsUpdateFreeze.MarkerPath);

    /// <summary>Переприменить заморозку, если маркер на месте. Возвращает описание сделанного
    /// (null — маркера нет, делать нечего).</summary>
    public static string? ReapplyIfMarked(IPowerShellRunner ps)
    {
        if (!IsMarked()) return null;

        try
        {
            var r = ps.Run(WindowsUpdateFreeze.BuildFreezeScript(), throwOnError: false,
                timeout: TimeSpan.FromMinutes(5));
            if (r.ExitCode != 0)
                return $"заморозка Windows Update НЕ переприменена (код {r.ExitCode}): {r.StdErr.Trim()}";

            // Сразу проверяем фактическое состояние: рапорт «применено» по факту записи в
            // реестр — ровно та ошибка, из-за которой п.72 и появился.
            var verify = ps.Run(WindowsUpdateFreeze.BuildVerifyScript(), throwOnError: false,
                timeout: TimeSpan.FromMinutes(3));
            var problems = WindowsUpdateFreeze.CheckApplied(verify.StdOut);
            return problems.Count == 0
                ? "заморозка Windows Update переприменена после ребута — проверено."
                : "заморозка Windows Update переприменена, но НЕ полностью: " + string.Join("; ", problems);
        }
        catch (Exception ex)
        {
            return $"заморозка Windows Update НЕ переприменена: {ex.Message}";
        }
    }

    /// <summary>Держит заморозку, а не проверяет однажды: на 260306 агент записал
    /// «переприменена — проверено» через секунду после применения, а через 32 секунды
    /// оркестратор поднял BITS и разморозил wuauserv (бэклог п.114). Пока висит маркер,
    /// раз в <paramref name="intervalSeconds"/> сверяется фактическое состояние; при дрейфе
    /// (блокирующие расхождения — службы/политика) заморозка переприменяется. Тишина в логе,
    /// пока всё держится.</summary>
    /// <param name="isMarked">Тест-шов; по умолчанию — маркер на диске.</param>
    public static Task StartHoldLoop(IPowerShellRunner ps, CancellationToken ct,
        Action<string, string?> announce, int intervalSeconds = 300, Func<bool>? isMarked = null) =>
        Task.Run(async () =>
        {
            var marked = isMarked ?? IsMarked;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (marked())
                    {
                        var verify = ps.Run(WindowsUpdateFreeze.BuildVerifyScript(),
                            throwOnError: false, timeout: TimeSpan.FromMinutes(3));
                        var check = WindowsUpdateFreeze.CheckAppliedDetailed(verify.StdOut);
                        if (!check.IsProtected)
                        {
                            announce("заморозка WU не удержалась (оркестратор?): "
                                + string.Join("; ", check.Blocking) + " — переприменяю.", null);
                            var r = ps.Run(WindowsUpdateFreeze.BuildFreezeScript(),
                                throwOnError: false, timeout: TimeSpan.FromMinutes(5));
                            announce(r.ExitCode == 0
                                ? "заморозка WU переприменена — слежу дальше."
                                : $"заморозка WU НЕ переприменена (код {r.ExitCode}).", null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    announce($"сторож заморозки WU: {ex.Message}", null);
                }

                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(intervalSeconds, 0) ) + TimeSpan.FromMilliseconds(50), ct); }
                catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);
}
