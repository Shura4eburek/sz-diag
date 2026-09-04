using System.Globalization;
using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>События питания из журнала клиента — то, что hub сам увидеть не может.
///
/// Боль (бэклог п.97, СЗ 160636): машина стояла в простое и ждала вырубона, `szcli reboots`
/// печатал «вырубонов не зафиксировано», а в журнале клиента лежал настоящий hard-off
/// 05.08 16:00:58 (плюс выключение кнопкой 04.08). Hub заводит событие только по смене
/// boot-time **при живом heartbeat**, поэтому всё, что случилось до подключения агента,
/// в таймлайн не попадало вовсе — и «не зафиксировано» читалось как «не было».
///
/// Агент приносит эти записи при подключении; hub сливает их со своими по времени.</summary>
public static class PowerEventsReader
{
    /// <summary>За какой период тянем историю. 30 дней — заявка живёт днями, а не месяцами,
    /// и тащить годовую историю в SQLite ни к чему.</summary>
    public const int DefaultDays = 30;

    /// <summary>Скрипт: по строке на событие — `<ISO-время>;<BugcheckCode>;<PowerButtonTimestamp>`
    /// для Kernel-Power 41 (hard-off/кнопка/BSOD — разбор тот же, что у
    /// <see cref="ShutdownClassifier"/>), либо `SLEEP;<ISO-время-начала-сна>;<секунды>` для пары
    /// Kernel-Power 42 (уход в сон) -> 107 (пробуждение). Без второго машина уходит в S3 при
    /// живой сессии, а hub считает её работающей — сутки «наблюдения» оказывались 7 часами
    /// реальной работы (бэклог п.140/222).</summary>
    public static string BuildScript(int days = DefaultDays) => $$"""
        $since = (Get-Date).AddDays(-{{days}})
        $events = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41; StartTime=$since } -ErrorAction SilentlyContinue)
        foreach ($e in $events) {
            $d = @{}
            try { $x = [xml]$e.ToXml(); foreach ($p in $x.Event.EventData.Data) { $d[$p.Name] = $p.'#text' } } catch { }
            "{0};{1};{2}" -f $e.TimeCreated.ToUniversalTime().ToString('o'), $d['BugcheckCode'], $d['PowerButtonTimestamp']
        }
        $pw = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=42,107; StartTime=$since } -ErrorAction SilentlyContinue) | Sort-Object TimeCreated
        $sleepStart = $null
        foreach ($e in $pw) {
            if ($e.Id -eq 42) { $sleepStart = $e.TimeCreated }
            elseif ($e.Id -eq 107 -and $sleepStart) {
                $dur = [int]($e.TimeCreated - $sleepStart).TotalSeconds
                "SLEEP;{0};{1}" -f $sleepStart.ToUniversalTime().ToString('o'), $dur
                $sleepStart = $null
            }
        }
        """;

    /// <summary>Разбор вывода скрипта. Вынесено ради тестируемости.</summary>
    public static IReadOnlyList<PowerEvent> Parse(string? stdout)
    {
        var result = new List<PowerEvent>();
        foreach (var raw in (stdout ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(';');

            if (parts[0] == "SLEEP")
            {
                if (parts.Length < 3) continue;
                if (!DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var sleepAt)) continue;
                var duration = long.TryParse(parts[2], out var d) ? d : (long?)null;
                result.Add(new PowerEvent(sleepAt, ShutdownKind.Sleep, 0, duration));
                continue;
            }

            if (parts.Length < 3) continue;
            if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at)) continue;

            var bugcheck = long.TryParse(parts[1], out var b) ? b : 0;
            var powerButton = ulong.TryParse(parts[2], out var p) ? p : 0;
            var kind = bugcheck != 0 ? ShutdownKind.Bsod
                : powerButton != 0 ? ShutdownKind.PowerButton
                : ShutdownKind.HardOff;

            // Код едет дальше: «BSOD ×13» без кодов не разделяет один почерк и три разных
            // дефекта (бэклог п.121).
            result.Add(new PowerEvent(at, kind, bugcheck));
        }
        return result;
    }

    /// <summary>Прочитать события с машины. Пустой список при любой ошибке: журнал —
    /// дополнение к наблюдению hub, а не критичный путь.</summary>
    public static IReadOnlyList<PowerEvent> Read(IPowerShellRunner ps, int days = DefaultDays)
    {
        try
        {
            var r = ps.Run(BuildScript(days), throwOnError: false, timeout: TimeSpan.FromMinutes(2));
            return r.ExitCode != 0 ? Array.Empty<PowerEvent>() : Parse(r.StdOut);
        }
        catch
        {
            return Array.Empty<PowerEvent>();
        }
    }
}
