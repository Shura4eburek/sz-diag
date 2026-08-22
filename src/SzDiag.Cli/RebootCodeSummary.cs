using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>Коды BSOD в `szcli reboots` (бэклог п.121): «BSOD ×13» не разделяет один почерк
/// и три разных дефекта, а код лежит в том же Kernel-Power 41 и раньше добывался ad-hoc
/// рецептом. Расшифровка — существующий <see cref="BugcheckCodes"/>.</summary>
public static class RebootCodeSummary
{
    /// <summary>Ячейка «Код» для строки таблицы: hex + имя для BSOD, прочерк для остальных.</summary>
    public static string FormatCode(RebootEvent evt)
        => evt.Bugcheck is { } code and > 0 ? BugcheckCodes.Format((uint)code) : "—";

    /// <summary>Сводка «сколько каких» по BSOD — повторяющийся почерк виден сразу.
    /// Пусто, если BSOD с кодами не было.</summary>
    public static IReadOnlyList<string> Build(IEnumerable<RebootEvent> events)
        => events
            .Where(e => e.Bugcheck is > 0)
            .GroupBy(e => e.Bugcheck!.Value)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{BugcheckCodes.Format((uint)g.Key)} ×{g.Count()}")
            .ToList();
}
