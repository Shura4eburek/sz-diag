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
/// Вторая боль (бэклог п.223, СЗ 161716): время отказа бралось из `TimeCreated` события
/// Kernel-Power 41, которое пишется только при СЛЕДУЮЩЕЙ загрузке — на 161716 это сдвинуло
/// пять отказов на 15–52 минуты и превратило «падает почти сразу после каждой загрузки»
/// в «падает под работой». Парный `EventLog` 6008 несёт настоящее время отказа в текстовых
/// `Properties[0]`/`Properties[1]` (время/дата), а 6005 — момент старта сеанса, от которого
/// считается длительность до отказа.
///
/// Агент приносит эти записи при подключении; hub сливает их со своими по времени.</summary>
public static class PowerEventsReader
{
    /// <summary>За какой период тянем историю. 30 дней — заявка живёт днями, а не месяцами,
    /// и тащить годовую историю в SQLite ни к чему.</summary>
    public const int DefaultDays = 30;

    /// <summary>Скрипт: по строке на событие —
    /// `<ISO-время>;<BugcheckCode>;<PowerButtonTimestamp>;<UptimeBeforeSeconds>`.
    /// Время — реальный момент отказа (из парного 6008, если найден; иначе `TimeCreated`
    /// события 41). Разбор классификации тот же, что у <see cref="ShutdownClassifier"/>:
    /// hard-off / кнопка / BSOD.</summary>
    public static string BuildScript(int days = DefaultDays) => $$"""
        $since = (Get-Date).AddDays(-{{days}})
        $events = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41; StartTime=$since } -ErrorAction SilentlyContinue)
        $dirty = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='EventLog'; Id=6008; StartTime=$since } -ErrorAction SilentlyContinue)
        $starts = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='EventLog'; Id=6005; StartTime=$since } -ErrorAction SilentlyContinue | Sort-Object TimeCreated)

        # 6008 is logged at the NEXT boot too, same as event 41 - but its message carries the
        # REAL failure moment as two localized strings (time, date) in Properties[0/1], not in
        # TimeCreated. Windows also sprinkles invisible bidi marks (U+200E/U+200F) into those
        # strings on some builds - strip them before parsing.
        function Get-RealShutdownTime($ev) {
            try {
                if ($ev.Properties.Count -lt 2) { return $null }
                $t = ("$($ev.Properties[0].Value)" -replace [char]0x200e,'' -replace [char]0x200f,'').Trim()
                $d = ("$($ev.Properties[1].Value)" -replace [char]0x200e,'' -replace [char]0x200f,'').Trim()
                [datetime]::Parse("$d $t")
            } catch { $null }
        }

        foreach ($e in $events) {
            $d = @{}
            try { $x = [xml]$e.ToXml(); foreach ($p in $x.Event.EventData.Data) { $d[$p.Name] = $p.'#text' } } catch { }

            # Match a 6008 written by the SAME boot (both land within minutes of each other at
            # startup) to recover the real failure time instead of TimeCreated.
            $pair = $dirty | Where-Object { [math]::Abs(($_.TimeCreated - $e.TimeCreated).TotalMinutes) -le 2 } | Select-Object -First 1
            $real = $null
            if ($pair) { $real = Get-RealShutdownTime $pair }
            $at = if ($real) { $real } else { $e.TimeCreated }

            # Session length: from the boot (6005) that immediately preceded the failure to the
            # failure itself - not boot-to-boot, which folds in the downtime while it sat off.
            $prevBoot = $starts | Where-Object { $_.TimeCreated -le $at } | Sort-Object TimeCreated -Descending | Select-Object -First 1
            $dur = ''
            if ($prevBoot) { $dur = [int]([TimeSpan]($at - $prevBoot.TimeCreated)).TotalSeconds }

            "{0};{1};{2};{3}" -f $at.ToUniversalTime().ToString('o'), $d['BugcheckCode'], $d['PowerButtonTimestamp'], $dur
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
            if (parts.Length < 3) continue;
            if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at)) continue;

            var bugcheck = long.TryParse(parts[1], out var b) ? b : 0;
            var powerButton = ulong.TryParse(parts[2], out var p) ? p : 0;
            var kind = bugcheck != 0 ? ShutdownKind.Bsod
                : powerButton != 0 ? ShutdownKind.PowerButton
                : ShutdownKind.HardOff;

            // Длительность сеанса до отказа (4-е поле) — опциональна ради обратной
            // совместимости со старыми версиями агента/записями без неё.
            long? uptimeBefore = parts.Length >= 4 && long.TryParse(parts[3], out var u) ? u : null;

            // Код едет дальше: «BSOD ×13» без кодов не разделяет один почерк и три разных
            // дефекта (бэклог п.121).
            result.Add(new PowerEvent(at, kind, bugcheck, uptimeBefore));
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
