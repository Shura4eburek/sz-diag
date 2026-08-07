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

    /// <summary>Ветки планировщика, откуда WU воскресает после ребута. На 160306 заморозка
    /// служб не пережила перезагрузку: `wuauserv` поднялся в `Start=3 Running` — `Start`
    /// восстанавливает не Medic, а именно оркестратор своими задачами (бэклог п.72).</summary>
    public static readonly string[] TaskFolders =
        { @"\Microsoft\Windows\UpdateOrchestrator\", @"\Microsoft\Windows\WindowsUpdate\" };

    /// <summary>Маркер заморозки на клиенте. Нужен, чтобы агент после ребута переприменил её
    /// сам: ребут — штатная часть долгой диагностики, а размороженный WU в этот момент
    /// приносит обновление и ещё один ребут, который потом читается как вырубон.</summary>
    public const string MarkerPath = @"C:\ProgramData\szdiag\wu-freeze.json";

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
        // Задачи оркестратора: их состояние тоже надо вернуть как было (п.72).
        foreach (var folder in TaskFolders)
            lines.Add($"Get-ScheduledTask -TaskPath '{folder}' -ErrorAction SilentlyContinue | " +
                      "ForEach-Object { 'task:' + $_.TaskPath + $_.TaskName + '=' + $_.State }");
        // Незавершённая транзакция — отдельный сигнал: замораживать машину, которая уже
        // в середине применения пакета, опасно (бэклог п.35b).
        lines.Add("'pending:' + (Test-Path 'C:\\Windows\\WinSxS\\pending.xml')");
        return string.Join("\n", lines);
    }

    /// <summary>Скрипт проверки: что на машине НА САМОМ ДЕЛЕ. `freeze` рапортовал успех по
    /// факту записи в реестр, а после ребута выяснялось, что службы живы (бэклог п.72).
    /// Печатает те же строки, что и capture, — читается тем же <see cref="ParseCapture"/>,
    /// плюс фактический статус служб (`state:wuauserv=Running`).</summary>
    public static string BuildVerifyScript()
    {
        var lines = new List<string> { "$ErrorActionPreference='SilentlyContinue'" };
        lines.Add("function Val($path,$name){ $v=(Get-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue).$name; if ($null -eq $v) { '' } else { $v } }");
        foreach (var svc in Services)
        {
            lines.Add($"'svc:{svc}=' + (Val 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\{svc}' 'Start')");
            lines.Add($"'state:{svc}=' + (Get-Service {svc} -ErrorAction SilentlyContinue).Status");
        }
        foreach (var (key, name) in PolicyValues())
            lines.Add($"'pol:{name}=' + (Val '{key}' '{name}')");
        foreach (var folder in TaskFolders)
            lines.Add($"Get-ScheduledTask -TaskPath '{folder}' -ErrorAction SilentlyContinue | " +
                      "ForEach-Object { 'task:' + $_.TaskPath + $_.TaskName + '=' + $_.State }");
        lines.Add($"'marker:' + (Test-Path '{MarkerPath}')");
        return string.Join("\n", lines);
    }

    /// <summary>Что из заморозки НЕ применилось. Пустой список — всё на месте.</summary>
    public static IReadOnlyList<string> CheckApplied(string verifyStdout)
    {
        var values = ParseCapture(verifyStdout);
        var problems = new List<string>();

        foreach (var svc in Services)
        {
            if (values.TryGetValue($"svc:{svc}", out var start) && start.Trim() != "4")
                problems.Add($"{svc}: Start={(string.IsNullOrWhiteSpace(start) ? "нет значения" : start)} (ожидалось 4)");
            if (values.TryGetValue($"state:{svc}", out var state)
                && state.Trim().Equals("Running", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{svc}: служба ЗАПУЩЕНА");
        }

        if (!values.TryGetValue("pol:NoAutoUpdate", out var noAuto) || noAuto.Trim() != "1")
            problems.Add("политика NoAutoUpdate не выставлена");
        if (!values.TryGetValue("pol:WUServer", out var wu) || !wu.Contains("127.0.0.1"))
            problems.Add("политика WUServer не указывает на несуществующий WSUS");

        var enabledTasks = values
            .Where(kv => kv.Key.StartsWith("task:", StringComparison.OrdinalIgnoreCase)
                         && kv.Value.Trim().Equals("Ready", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key["task:".Length..])
            .ToList();
        if (enabledTasks.Count > 0)
            problems.Add($"задачи оркестратора включены ({enabledTasks.Count}): "
                         + string.Join(", ", enabledTasks.Take(5)));

        return problems;
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

        // Второй эшелон, переживающий ребут: задачи оркестратора. Именно они поднимают
        // `wuauserv` обратно в `Start=3 Running` после перезагрузки (бэклог п.72).
        foreach (var folder in TaskFolders)
            lines.Add($"Get-ScheduledTask -TaskPath '{folder}' -ErrorAction SilentlyContinue | " +
                      "Disable-ScheduledTask -ErrorAction SilentlyContinue | Out-Null");

        // Маркер для агента: после ребута он переприменит заморозку сам, не дожидаясь оператора.
        lines.Add($"New-Item -ItemType Directory -Force -Path (Split-Path '{MarkerPath}') | Out-Null");
        lines.Add($"@{{ frozenAt = (Get-Date).ToString('o') }} | ConvertTo-Json | " +
                  $"Set-Content -Path '{MarkerPath}' -Encoding utf8");
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
        // Задачи оркестратора: включаем обратно те, что были готовы к запуску до нас.
        // Машина обязана уехать к клиенту с работающими обновлениями безопасности.
        foreach (var kv in previous.Where(p => p.Key.StartsWith("task:", StringComparison.OrdinalIgnoreCase)))
        {
            if (!kv.Value.Trim().Equals("Ready", StringComparison.OrdinalIgnoreCase)) continue;
            var full = kv.Key["task:".Length..];
            var slash = full.LastIndexOf('\\');
            if (slash < 0) continue;
            var path = full[..(slash + 1)].Replace("'", "''");
            var name = full[(slash + 1)..].Replace("'", "''");
            lines.Add($"Enable-ScheduledTask -TaskPath '{path}' -TaskName '{name}' -ErrorAction SilentlyContinue | Out-Null");
        }
        // Прежних значений не знаем (замораживали с другой машины) — включаем всё, что нашли:
        // оставленная выключенной задача WU опаснее лишнего включения.
        if (!previous.Keys.Any(k => k.StartsWith("task:", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var folder in TaskFolders)
                lines.Add($"Get-ScheduledTask -TaskPath '{folder}' -ErrorAction SilentlyContinue | " +
                          "Enable-ScheduledTask -ErrorAction SilentlyContinue | Out-Null");
        }

        foreach (var svc in Services)
            lines.Add($"Start-Service {svc} -ErrorAction SilentlyContinue");
        lines.Add($"Remove-Item -Path '{MarkerPath}' -Force -ErrorAction SilentlyContinue");
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
