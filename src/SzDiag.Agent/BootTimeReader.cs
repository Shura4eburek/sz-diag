using System.Globalization;

namespace SzDiag.Agent;

/// <summary>Время загрузки ОС клиента — hub по нему отличает реальный ребут от лага heartbeat.
/// Читается ОДИН раз при старте агента: значение не меняется, пока машина не перезагрузилась,
/// а после ребута агент стартует заново и прочитает новое.</summary>
public static class BootTimeReader
{
    /// <summary>Возвращает время загрузки ОС или null, если получить не удалось.</summary>
    /// <remarks>Намеренно НЕ используем <c>Environment.TickCount64</c> даже как фоллбэк:
    /// он не идёт во время сна (S3/S4), поэтому после пробуждения расчётный boot-time
    /// «уезжает вперёд» и hub увидит ребут там, где машина просто поспала. Лучше отдать
    /// null (признак «неизвестно»), чем ложный ребут.</remarks>
    public static DateTimeOffset? Read(IPowerShellRunner ps)
    {
        try
        {
            var r = ps.Run(
                "(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o')",
                throwOnError: false,
                timeout: TimeSpan.FromSeconds(30));
            if (r.ExitCode != 0) return null;
            return Parse(r.StdOut);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Разбор ISO-8601 ("o") из вывода PowerShell. Вынесено ради тестируемости.</summary>
    public static DateTimeOffset? Parse(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;
        return DateTimeOffset.TryParse(line, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;
    }
}
