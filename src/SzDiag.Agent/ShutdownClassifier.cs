using System.Globalization;
using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Чем закончилась прошлая сессия ОС: настоящий обрыв питания, нажатие кнопки,
/// BSOD или штатное выключение.
///
/// Боль (бэклог п.93, СЗ 161312): hub заводит вырубон по любой смене boot-time, поэтому
/// «выключили кнопкой» и «оборвалось питание» попадали в один счётчик `⚡N`. Два события из
/// пяти оказались нажатием кнопки — вердикт по заявке менялся вместе с ними. Дискриминатор
/// лежит прямо в Kernel-Power 41: `PowerButtonTimestamp != 0` = кнопка, `BugcheckCode != 0` =
/// BSOD, ноль в обоих = жёсткий обрыв.</summary>
public static class ShutdownClassifier
{
    /// <summary>Насколько событие 41 может отстоять от boot-time, чтобы считаться относящимся
    /// к ЭТОЙ загрузке. Windows пишет его уже после старта, обычно в первые секунды; берём
    /// запас на медленный старт журнала.</summary>
    private static readonly TimeSpan SameBootWindow = TimeSpan.FromMinutes(10);

    /// <summary>Скрипт: последнее Kernel-Power 41 одной строкой
    /// <c>&lt;ISO-время&gt;;&lt;BugcheckCode&gt;;&lt;PowerButtonTimestamp&gt;</c>.
    /// Пусто — событий нет (штатное выключение).</summary>
    public const string Script = """
        $e = Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41 } -MaxEvents 1 -ErrorAction SilentlyContinue
        if ($e) {
            $x = [xml]$e.ToXml(); $d = @{}
            foreach ($p in $x.Event.EventData.Data) { $d[$p.Name] = $p.'#text' }
            "{0};{1};{2}" -f $e.TimeCreated.ToUniversalTime().ToString('o'), $d['BugcheckCode'], $d['PowerButtonTimestamp']
        }
        """;

    /// <summary>Прочитать и классифицировать. Любая ошибка — <see cref="ShutdownKind.Unknown"/>:
    /// «не знаем» честнее, чем ложный вырубон.</summary>
    public static string Read(IPowerShellRunner ps, DateTimeOffset? bootTime)
    {
        try
        {
            var r = ps.Run(Script, throwOnError: false, timeout: TimeSpan.FromSeconds(60));
            return r.ExitCode != 0 ? ShutdownKind.Unknown : Classify(r.StdOut, bootTime);
        }
        catch
        {
            return ShutdownKind.Unknown;
        }
    }

    /// <summary>Разбор строки скрипта. Вынесено ради тестируемости.</summary>
    public static string Classify(string? stdout, DateTimeOffset? bootTime)
    {
        var line = (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

        // Событий 41 нет вовсе — машину выключали штатно (или журнал чист).
        if (string.IsNullOrEmpty(line)) return ShutdownKind.Clean;

        var parts = line.Split(';');
        if (parts.Length < 3) return ShutdownKind.Unknown;

        if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var at))
            return ShutdownKind.Unknown;

        // Событие из прошлой жизни машины к текущей загрузке отношения не имеет: последний
        // старт был штатным, а 41 висит с позапрошлой недели.
        if (bootTime is { } boot && (at - boot).Duration() > SameBootWindow)
            return ShutdownKind.Clean;

        var bugcheck = long.TryParse(parts[1], out var b) ? b : 0;
        var powerButton = ulong.TryParse(parts[2], out var p) ? p : 0;

        if (bugcheck != 0) return ShutdownKind.Bsod;
        if (powerButton != 0) return ShutdownKind.PowerButton;
        return ShutdownKind.HardOff;
    }
}
