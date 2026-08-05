using System.Runtime.InteropServices;

namespace SzDiag.Agent;

/// <summary>Не даёт машине уснуть, пока агент работает.
///
/// Боль (СЗ 160705, бэклог п.58): клиентская машина ушла в сон **посреди диагностики** —
/// `diag.md` не появился вовсе, а агент после пробуждения перестал отвечать на exec, хотя
/// снаружи выглядел здоровым (heartbeat шёл, `list` показывал «готов»). На заявке
/// «вимикається під навантаженням» уснувшая машина ещё и путает картину: «отвалилась»
/// неотличимо от «вырубилась».
///
/// Сделано **без правки схемы питания**: удерживаем power request на время жизни процесса
/// (<c>SetThreadExecutionState</c>). Это ровно в духе инварианта «откат без следов» — нечего
/// откатывать: запрос исчезает вместе с процессом, в системе не остаётся ни изменённых
/// таймаутов, ни выключенной гибернации, и в <see cref="RevertState"/> не нужен парный флаг.
///
/// Ограничение честно: удержание блокирует **простойный** сон. Сон по кнопке/крышке или
/// принудительный `shutdown /h` оно не отменяет — но именно они на заявках и не мешали.</summary>
public static class SleepGuard
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    /// <summary>Запросить «система нужна» до отмены. false — ОС отказала (или не Windows).</summary>
    /// <param name="keepDisplayOn">Держать и экран: нужно для GUI-тулов (FurMark/TM5 рисуют
    /// окно), которые на погашенном экране ведут себя иначе.</param>
    public static bool Prevent(bool keepDisplayOn = false)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var flags = ExecutionState.Continuous | ExecutionState.SystemRequired;
        if (keepDisplayOn) flags |= ExecutionState.DisplayRequired;
        try { return SetThreadExecutionState(flags) != 0; }
        catch { return false; }
    }

    /// <summary>Снять удержание (машина снова может засыпать по своим правилам).</summary>
    public static bool Allow()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return SetThreadExecutionState(ExecutionState.Continuous) != 0; }
        catch { return false; }
    }
}
