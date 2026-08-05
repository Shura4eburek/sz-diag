namespace SzDiag.Contracts;

/// <summary>Заморозка Windows Update на время сессии — и парная разморозка.
///
/// Боль (СЗ 160636, бэклог п.34b): машину только что вытащили из кирпича, вызванного
/// сорванным обновлением, — и за четыре часа она **сама себя загнала в то же состояние**:
/// WU скачал и начал ставить два LCU, оставив `pending.xml` на 46,5 МБ. Обнаружилось
/// случайно, на этапе уборки следов.
///
/// «По-простому» заморозка не держится: 31.07 её поставили (`NoAutoUpdate=1`, пауза,
/// `wuauserv` → disabled), а 04.08 машина поднялась — и через 2 минуты WU снова ставил
/// пакеты. Службу воскресил **`WaaSMedicSvc`** («лекарь» WU, его прямая задача — откатывать
/// такие правки), `wuauserv` при этом оказался в `Manual`, а не `Disabled`.
///
/// Поэтому рабочий набор именно такой:
/// 1. `Start=4` **в реестре** для `wuauserv`, `UsoSvc`, `WaaSMedicSvc` — `sc config` на
///    медика не проходит, реестр берёт;
/// 2. политика `WUServer`/`WUStatusServer` на **несуществующий WSUS** (`127.0.0.1:8530`) +
///    `UseWUServer=1` — самый надёжный глушитель: клиенту просто некуда идти, и это
///    переживает воскрешение служб;
/// 3. `NoAutoUpdate=1`, `AUOptions=1`, `DoNotConnectToWindowsUpdateInternetLocations=1`.
///
/// Все прежние значения складываются в state, чтобы `unfreeze` вернул ровно как было:
/// машина обязана уехать к клиенту с работающими обновлениями безопасности.</summary>
public static class WindowsUpdateFreeze
{
    /// <summary>Службы, которые надо погасить именно через реестр.</summary>
    public static readonly string[] Services = { "wuauserv", "UsoSvc", "WaaSMedicSvc" };

    private const string PolicyKey = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string AuKey = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";

    /// <summary>Скрипт снятия текущего состояния — печатает строки вида
    /// <c>svc:wuauserv=2</c> и <c>pol:NoAutoUpdate=1</c> (или <c>=</c>, если значения нет).
    /// Нужен, чтобы `unfreeze` вернул ровно прежнее, а не «дефолт по нашему разумению».</summary>
    public static string BuildCaptureScript()
    {
        var lines = new List<string>
        {
            "$ErrorActionPreference='SilentlyContinue'",
            "function Val($path,$name){ $v=(Get-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue).$name; if ($null -eq $v) { '' } else { $v } }",
        };
        foreach (var svc in Services)
            lines.Add($"'svc:{svc}=' + (Val 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\{svc}' 'Start')");
        foreach (var (key, name) in PolicyValues())
            lines.Add($"'pol:{name}=' + (Val '{key}' '{name}')");
        // Незавершённая транзакция — отдельный сигнал: замораживать машину, которая уже
        // в середине применения пакета, опасно (бэклог п.35b).
        lines.Add("'pending:' + (Test-Path 'C:\\Windows\\WinSxS\\pending.xml')");
        return string.Join("\n", lines);
    }

    /// <summary>Скрипт заморозки.</summary>
    public static string BuildFreezeScript()
    {
        var lines = new List<string> { "$ErrorActionPreference='SilentlyContinue'" };
        foreach (var svc in Services)
        {
            lines.Add($"Stop-Service {svc} -Force -ErrorAction SilentlyContinue");
            lines.Add($"Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\{svc}' " +
                      "-Name Start -Value 4 -Type DWord -Force");
        }
        lines.Add($"New-Item -Path '{PolicyKey}' -Force | Out-Null");
        lines.Add($"New-Item -Path '{AuKey}' -Force | Out-Null");
        lines.Add($"Set-ItemProperty -Path '{PolicyKey}' -Name DoNotConnectToWindowsUpdateInternetLocations -Value 1 -Type DWord -Force");
        lines.Add($"Set-ItemProperty -Path '{PolicyKey}' -Name WUServer -Value 'http://127.0.0.1:8530' -Type String -Force");
        lines.Add($"Set-ItemProperty -Path '{PolicyKey}' -Name WUStatusServer -Value 'http://127.0.0.1:8530' -Type String -Force");
        lines.Add($"Set-ItemProperty -Path '{AuKey}' -Name UseWUServer -Value 1 -Type DWord -Force");
        lines.Add($"Set-ItemProperty -Path '{AuKey}' -Name NoAutoUpdate -Value 1 -Type DWord -Force");
        lines.Add($"Set-ItemProperty -Path '{AuKey}' -Name AUOptions -Value 1 -Type DWord -Force");
        lines.Add("'frozen'");
        return string.Join("\n", lines);
    }

    /// <summary>Скрипт разморозки: возвращает снятые значения; чего не было — удаляет.
    /// Заморозка, оставшаяся на машине клиента, означает машину без обновлений безопасности,
    /// поэтому эта половина не менее обязательна, чем первая.</summary>
    public static string BuildUnfreezeScript(IReadOnlyDictionary<string, string> previous)
    {
        var lines = new List<string> { "$ErrorActionPreference='SilentlyContinue'" };
        foreach (var svc in Services)
        {
            var key = $"svc:{svc}";
            var path = $"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\{svc}";
            // Не знаем прежнего — ставим 3 (Manual): это дефолт Windows для всех трёх,
            // и это заведомо лучше, чем оставить 4 (Disabled).
            var value = previous.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : "3";
            lines.Add($"Set-ItemProperty -Path '{path}' -Name Start -Value {value} -Type DWord -Force");
        }
        foreach (var (key, name) in PolicyValues())
        {
            var prevKey = $"pol:{name}";
            if (previous.TryGetValue(prevKey, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                var isString = name is "WUServer" or "WUStatusServer";
                lines.Add(isString
                    ? $"Set-ItemProperty -Path '{key}' -Name {name} -Value '{value}' -Type String -Force"
                    : $"Set-ItemProperty -Path '{key}' -Name {name} -Value {value} -Type DWord -Force");
            }
            else
            {
                lines.Add($"Remove-ItemProperty -Path '{key}' -Name {name} -ErrorAction SilentlyContinue");
            }
        }
        foreach (var svc in Services)
            lines.Add($"Start-Service {svc} -ErrorAction SilentlyContinue");
        lines.Add("'unfrozen'");
        return string.Join("\n", lines);
    }

    /// <summary>Разбор вывода <see cref="BuildCaptureScript"/> в словарь.</summary>
    public static Dictionary<string, string> ParseCapture(string stdout)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (stdout ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var sep = line.IndexOf('=');
            if (sep <= 0) continue;
            result[line[..sep]] = line[(sep + 1)..].Trim();
        }
        return result;
    }

    /// <summary>Есть ли на машине незавершённая транзакция обновления (по выводу capture).</summary>
    public static bool HasPendingTransaction(string stdout)
        => (stdout ?? "").Contains("pending:True", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Key, string Name)> PolicyValues()
    {
        yield return (PolicyKey, "DoNotConnectToWindowsUpdateInternetLocations");
        yield return (PolicyKey, "WUServer");
        yield return (PolicyKey, "WUStatusServer");
        yield return (AuKey, "UseWUServer");
        yield return (AuKey, "NoAutoUpdate");
        yield return (AuKey, "AUOptions");
    }
}
