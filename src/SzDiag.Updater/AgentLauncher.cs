using System.ComponentModel;
using System.Diagnostics;

namespace SzDiag.Updater;

/// <summary>Запуск agent.exe в той же консоли (без redirect stdio — агент интерактивно
/// спрашивает номер СЗ). Updater ждёт выхода агента и возвращает его код.</summary>
public static class AgentLauncher
{
    // ERROR_ELEVATION_REQUIRED — agent.exe помечен requireAdministrator в манифесте.
    private const int ErrorElevationRequired = 740;

    public static int LaunchAndWait(string agentExePath, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = agentExePath,
            WorkingDirectory = workingDir,
            UseShellExecute = false, // наследуем консоль родителя
        };
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            Console.Error.WriteLine(
                "Агенту нужны права администратора. Запусти SzDiag.Updater.exe от имени администратора.");
            return 4;
        }
    }
}
