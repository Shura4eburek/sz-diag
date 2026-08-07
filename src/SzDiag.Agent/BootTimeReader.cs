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
            // Берём НЕ абсолютное время загрузки, а аптайм — разность двух локальных времён.
            // Она не зависит от таймзоны, а вот `LastBootUpTime.ToString('o')` зависит: WinPE
            // стартует с дефолтной таймзоной (Pacific), и boot-time уезжал на 11 часов в
            // будущее — uptime в `list` показывал «0сек», а hub считал по нему вырубоны
            // (бэклог п.90).
            var r = ps.Run(
                "((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalSeconds",
                throwOnError: false,
                timeout: TimeSpan.FromSeconds(30));
            if (r.ExitCode != 0) return null;
            return ParseUptimeSeconds(r.StdOut, DateTimeOffset.Now);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Из аптайма в секундах — момент загрузки по часам самого агента.</summary>
    public static DateTimeOffset? ParseUptimeSeconds(string? stdout, DateTimeOffset now)
    {
        var line = (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;

        // Локаль клиента печатает запятую как десятичный разделитель.
        if (!double.TryParse(line.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds))
            return null;

        // Отрицательный аптайм означает битые часы — лучше «неизвестно», чем ложный ребут.
        if (seconds < 0) return null;

        // Округляем до секунды: значение считается от «сейчас», и без округления два запуска
        // агента подряд дали бы разные boot-time. Hub сравнивает с допуском (см.
        // SessionRegistry.RebootTolerance), но лишний шум в данных ни к чему.
        return new DateTimeOffset(
            new DateTime(now.AddSeconds(-seconds).DateTime.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
                DateTimeKind.Unspecified),
            now.Offset);
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
