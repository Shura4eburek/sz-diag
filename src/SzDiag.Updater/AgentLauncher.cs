using System.ComponentModel;
using System.Diagnostics;

namespace SzDiag.Updater;

/// <summary>Запуск agent.exe в той же консоли (без redirect stdio — агент интерактивно
/// спрашивает номер СЗ, если его не передали). Updater ждёт выхода агента и возвращает
/// его код.
///
/// Аргументы апдейтера пробрасываются агенту как есть: `SzDiag.Updater.exe 164266`
/// поднимает агента сразу под нужной СЗ, без вопроса в консоль. Это нужно для
/// автозапуска (RunOnce/ярлык/задача), где отвечать на вопрос некому — на 164266 машину
/// после отката зависших обновлений надо было вернуть в ту же заявку при первом входе
/// в систему.</summary>
public static class AgentLauncher
{
    // ERROR_ELEVATION_REQUIRED — agent.exe помечен requireAdministrator в манифесте.
    private const int ErrorElevationRequired = 740;

    /// <summary>Чистит аргументы перед передачей агенту: пустые и пробельные выкидывает,
    /// у остальных срезает края. Хвостовой пробел из ярлыка или RunOnce превращал бы
    /// номер СЗ в «некорректный» (`SzNumber` требует ровно шесть цифр).</summary>
    public static IReadOnlyList<string> Sanitize(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            result.Add(a.Trim());
        }
        return result;
    }

    public static int LaunchAndWait(string agentExePath, string workingDir,
        IReadOnlyList<string>? args = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = agentExePath,
            WorkingDirectory = workingDir,
            UseShellExecute = false, // наследуем консоль родителя
        };
        // ArgumentList, а не строка: кавычки и пробелы экранирует рантайм.
        foreach (var a in Sanitize(args ?? Array.Empty<string>())) psi.ArgumentList.Add(a);
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
