using System.Diagnostics;

namespace SzDiag.Updater;

/// <summary>Запуск agent.exe в той же консоли (без redirect stdio — агент интерактивно
/// спрашивает номер СЗ). Updater ждёт выхода агента и возвращает его код.</summary>
public static class AgentLauncher
{
    public static int LaunchAndWait(string agentExePath, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = agentExePath,
            WorkingDirectory = workingDir,
            UseShellExecute = false, // наследуем консоль родителя
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
