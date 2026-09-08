using System.Runtime.InteropServices;

namespace SzDiag.Updater;

/// <summary>Держит окно консоли открытым, когда апдейтер вышел с ошибкой, а окно
/// принадлежит только ему (двойной клик из проводника).
///
/// Родилось на 161642: апдейтер лежал на рабочем столе клиента, а рабочий стол в Windows 11
/// по умолчанию синхронизируется в OneDrive. <see cref="CloudInstallGuard"/> отработал ровно
/// как задумано — напечатал причину и вышел с кодом 4, — но прочитать это было невозможно:
/// окно закрывалось вместе с процессом. День ушёл на «окно появляется и сразу пропадает»
/// при том, что программа честно писала ответ в этом самом окне.</summary>
public static class ConsolePause
{
    /// <summary>Ждать ли нажатия перед выходом.
    ///
    /// <paramref name="processesOnConsole"/> — сколько процессов присоединено к консоли:
    /// 1 = консоль создана нами (двойной клик), больше = запуск из cmd/powershell, где окно
    /// и так останется. При перенаправленном вводе не ждём никогда — пауза повесила бы
    /// скрипт (в т.ч. <c>updater-log.cmd</c>) навсегда.</summary>
    public static bool ShouldWait(int exitCode, int processesOnConsole, bool inputRedirected)
        => exitCode != 0 && processesOnConsole <= 1 && !inputRedirected;

    /// <summary>То же, но само выясняет обстановку у Windows.</summary>
    public static bool ShouldWait(int exitCode)
        => ShouldWait(exitCode, CountProcessesOnConsole(), Console.IsInputRedirected);

    /// <summary>Сколько процессов сидит на нашей консоли. При любой неожиданности возвращаем
    /// 2 («не наше окно») — лишняя пауза хуже отсутствующей: она способна подвесить
    /// автоматический запуск.</summary>
    private static int CountProcessesOnConsole()
    {
        try
        {
            var buffer = new uint[8];
            var count = GetConsoleProcessList(buffer, (uint)buffer.Length);
            return count == 0 ? 2 : (int)count;
        }
        catch { return 2; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
