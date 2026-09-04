namespace SzDiag.Contracts;

/// <summary>Следы, которые мы оставляем на клиентской машине, и их уборка.
///
/// Три живых боли в одном месте:
/// * **п.88** — `lhmmon` грузит kernel-драйвер под именем **`R0lhmmon`** (не `lhmmon`!), из-за
///   чего `.sys` держится, папка не удаляется, а в реестре навсегда остаётся
///   `HKLM\SYSTEM\CurrentControlSet\Services\R0lhmmon`;
/// * **п.56** — от прогонов 27–28.07 на 160306 остались `iotest.bin` на 12 ГБ, CSV сенсоров и
///   задачи `szdiag-iostress`/`szdiag-lhmmon` в состоянии Ready;
/// * **п.99** — задача `szdiag-lhmmon` **без номера СЗ**: из имени не понять, чья она, и
///   уборка по маске `szdiag-*-<СЗ>` её не находит.
///
/// Инвариант из CLAUDE.md — «доступ откатывается без следов» — распространяется и на это.</summary>
public static class ClientTraces
{
    /// <summary>Префикс всех наших задач. Единое место, чтобы имена не расходились по коду
    /// и рецептам (п.99).</summary>
    public const string TaskPrefix = "szdiag";

    /// <summary>Имя задачи по единому правилу `szdiag-<роль>-<СЗ>`. Задача без номера СЗ —
    /// это хвост, которого никто не найдёт.</summary>
    public static string TaskName(string role, string sz) => $"{TaskPrefix}-{role}-{sz}";

    /// <summary>Драйверы/сервисы, которые оставляют наши инструменты. Имя сервиса у LHM
    /// начинается с `R0` — по «очевидному» `lhmmon` уборка промахивалась (п.88).</summary>
    public static readonly string[] ToolServices = { "R0lhmmon", "WinRing0_1_2_0", "R0OCCT" };

    /// <summary>Корень вывода фоновых exec-задач на клиенте (`BackgroundJobs` кладёт сюда
    /// `<jobId>\out.txt`/`err.txt`) — общая константа, чтобы CLI (`exec --result --save`,
    /// бэклог п.214) и уборка следов ссылались на один и тот же путь, а не на два его
    /// текстовых дубля.</summary>
    public const string JobsRoot = @"C:\ProgramData\szdiag\jobs";

    /// <summary>Наши временные каталоги на клиенте: вывод фоновых задач, CSV наблюдателя и
    /// доставленные инструменты (`ToolsDirectory.Resolve` уводит их сюда, когда папка агента
    /// сама оказалась в OneDrive/Dropbox/… — иначе четверть гига OCCT+lhmmon уезжала в личное
    /// облако клиента и оставалась там навсегда, бэклог п.63). Всё это заведомо наше —
    /// чистится без вопросов.</summary>
    /// <summary>`tools` сюда сознательно не входит: он раскладывается по инструментам
    /// отдельным блоком (`tool:` в инвентаре) — иначе клиент видел бы одну цифру суммы
    /// вместо «prime95 34 МБ, lhmmon 67 МБ» (бэклог п.158).</summary>
    public static readonly string[] TempDirs =
    {
        JobsRoot,
        @"C:\ProgramData\szdiag\sensors",
    };

    /// <summary>Оба возможных места, куда `push`/`ToolsDirectory.Resolve` кладёт инструменты:
    /// рядом с агентом (обычный случай) и в ProgramData (агент внутри OneDrive/Dropbox — п.63).
    /// Убираются целиком по имени папки инструмента, а не одной суммой байт.</summary>
    public const string CloudFallbackToolsDir = @"C:\ProgramData\szdiag\tools";

    /// <summary>Рабочие папки, куда рецепты пишут логи/CSV мимо `ProgramData\szdiag` и мимо
    /// `tools\` (бэклог п.158, СЗ 160306): после «уборки» на клиенте оставалось 101 МБ наших
    /// бинарей и рабочая папка OCCT — `client info` их даже не показывал.</summary>
    public static readonly string[] RecipeWorkDirs = { @"C:\OCCT" };

    /// <summary>Что осталось на машине: задачи с нашим префиксом (в том числе безымянные, без
    /// номера СЗ), загруженные драйверы инструментов, размеры наших каталогов и крупные файлы
    /// прогонов. Печатает строки `key=value`, чтобы разбирать одним парсером.</summary>
    public static string BuildInventoryScript()
    {
        var services = string.Join(",", ToolServices.Select(s => $"'{s}'"));
        var dirs = string.Join(",", TempDirs.Select(d => $"'{d}'"));
        var recipeDirs = string.Join(",", RecipeWorkDirs.Select(d => $"'{d}'"));
        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Get-ScheduledTask | Where-Object { $_.TaskName -like '{{TaskPrefix}}*' } |
                ForEach-Object { 'task:' + $_.TaskName + '=' + $_.State }
            foreach ($svc in @({{services}})) {
                $key = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $svc
                $present = Test-Path $key
                $running = (Get-Service -Name $svc -ErrorAction SilentlyContinue).Status
                'service:' + $svc + '=' + $(if ($present) { "$running/registered" } else { 'none' })
            }
            foreach ($dir in @({{dirs}})) {
                if (Test-Path $dir) {
                    $size = (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue |
                        Measure-Object -Property Length -Sum).Sum
                    'dir:' + $dir + '=' + [math]::Round(($size / 1MB), 1)
                } else { 'dir:' + $dir + '=none' }
            }
            foreach ($dir in @({{recipeDirs}})) {
                if (Test-Path $dir) {
                    $size = (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue |
                        Measure-Object -Property Length -Sum).Sum
                    'dir:' + $dir + '=' + [math]::Round(($size / 1MB), 1)
                }
            }
            # Крупные артефакты прогонов (iotest.bin на 12 ГБ и подобное) — только показываем:
            # удалять чужие файлы по маске нельзя, решение за оператором.
            Get-ChildItem 'C:\ProgramData\szdiag' -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Length -gt 500MB } |
                ForEach-Object { 'big:' + $_.FullName + '=' + [math]::Round(($_.Length / 1GB), 1) }
            # Фактический путь к логу агента: чек-лист отсылал к «agent.log рядом с exe», лога
            # там нет (он в logs\), и родился ложный вывод «агент не пишет лог» (п.117).
            $pp = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction SilentlyContinue).ParentProcessId
            $agent = (Get-Process -Id $pp -ErrorAction SilentlyContinue).Path
            if ($agent -and (Split-Path $agent -Leaf) -eq 'agent.exe') {
                'log:' + (Join-Path (Split-Path $agent) 'logs\agent.log')
            }
            # Живы ли perf-счётчики (бэклог п.201): "загрузка диска 100%" в диспетчере задач и
            # рецепты (Get-Counter, Win32_PerfRawData_*) опираются на этот же источник. На 161972
            # он был разрушен целиком - Invalid class (0x80041010) - а рецепт при этом молча
            # рапортовал успехом с пустой таблицей. Проверяем именно ту WMI-ветку, которая ломается.
            try {
                $null = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -ErrorAction Stop | Select-Object -First 1
                'perf:ok'
            } catch {
                'perf:broken=' + $_.Exception.Message
            }
            # tools\ рядом с агентом (обычный, НЕ облачный случай) — раньше уборка знала только
            # про ProgramData\szdiag\tools (фоллбэк для OneDrive), и prime95/lhmmon оставались
            # на диске навсегда, а client info про них молчал (бэклог п.158, СЗ 160306).
            if ($agent) {
                $agentToolsDir = Join-Path (Split-Path $agent) 'tools'
                'agenttoolsdir:' + $agentToolsDir
                if (Test-Path $agentToolsDir) {
                    Get-ChildItem $agentToolsDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                        $tsize = (Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue |
                            Measure-Object -Property Length -Sum).Sum
                        'tool:' + $_.Name + '=' + [math]::Round(($tsize / 1MB), 1)
                    }
                }
            }
            # То же самое для C:\ProgramData\szdiag\tools (облачный фоллбэк) — по инструментам,
            # а не одной цифрой суммы, чтобы info называл их по именам ('доставлені тули: …').
            if (Test-Path 'C:\ProgramData\szdiag\tools') {
                Get-ChildItem 'C:\ProgramData\szdiag\tools' -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                    $tsize = (Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue |
                        Measure-Object -Property Length -Sum).Sum
                    'tool:' + $_.Name + '=' + [math]::Round(($tsize / 1MB), 1)
                }
            }
            """;
    }

    /// <summary>Процессы стресс-тулов, включая подтесты, что переживают закрытие оболочки
    /// (бэклог п.213), и `lhmmon` — обычная уборка его сознательно не трогает (наблюдатель
    /// должен жить, пока идёт тест), но «снять ВСЮ нагрузку разом» обязан выключить и его
    /// тоже: иначе `wipe-tools` спотыкается о занятый файл (бэклог п.126/183).</summary>
    public static readonly string[] StressProcessNames =
    {
        "OCCTCmd", "OCCT", "furmark", "TM5", "3DMarkCmd", "prime95", "y-cruncher", "Kagari",
        "linpack", "gpu3d-Win64-Shipping", "gpu_unreal", "memtest*", "lhmmon",
    };

    /// <summary>Общая часть уборки/аварийной остановки: снять наши задачи планировщика (кроме
    /// `keepTasks` — рабочего доступа текущей сессии) и выгрузить драйверы инструментов.</summary>
    private static string TasksAndDriversScript(IReadOnlyList<string>? keepTasks)
    {
        var keep = string.Join(",", (keepTasks ?? Array.Empty<string>()).Select(t => $"'{t}'"));
        var services = string.Join(",", ToolServices.Select(s => $"'{s}'"));
        var jobsDir = TempDirs[0].Replace("'", "''");
        return $$"""
            $keep = @({{keep}})
            # Изолированная (scheduled-task) фоновая задача переживает падение агента специально
            # (бэклог п.53), но снятие самой задачи ниже НЕ убивает дерево процессов —
            # без этого шага OCCT/TM5 под SYSTEM оставался живым после close (Critical-4,
            # ревью волны 1: "весь доступ на клиенте временный и откатывается без следов").
            # Бьём по рабочему каталогу задач целиком, а не по конкретному jobId — за сессию
            # изолированных задач могло быть несколько.
            Get-CimInstance Win32_Process -Filter "Name like '%powershell%'" -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandLine -like '*{{jobsDir}}*' } |
                ForEach-Object {
                    taskkill /PID $_.ProcessId /T /F | Out-Null
                    'убит процесс изолированной задачи: ' + $_.ProcessId
                }
            Get-ScheduledTask | Where-Object { $_.TaskName -like '{{TaskPrefix}}*' -and $keep -notcontains $_.TaskName } |
                ForEach-Object {
                    Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
                    'снята задача: ' + $_.TaskName
                }
            foreach ($svc in @({{services}})) {
                if (Test-Path ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $svc)) {
                    & sc.exe stop $svc | Out-Null
                    & sc.exe delete $svc | Out-Null
                    'снят драйвер: ' + $svc
                }
            }
            """;
    }

    /// <summary>Уборка: снять задачи с нашим префиксом, выгрузить и удалить драйверы
    /// инструментов, вычистить наши временные каталоги.</summary>
    /// <param name="keepTasks">Задачи, которые снимать НЕЛЬЗЯ (текущая сессия: sshd, watchdog,
    /// автостарт) — иначе уборка обрубит доступ сама себе.</param>
    public static string BuildCleanupScript(IReadOnlyList<string>? keepTasks = null)
    {
        var dirs = string.Join(",", TempDirs.Select(d => $"'{d}'"));
        var recipeDirs = string.Join(",", RecipeWorkDirs.Select(d => $"'{d}'"));
        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            {{TasksAndDriversScript(keepTasks)}}
            foreach ($dir in @({{dirs}})) {
                if (Test-Path $dir) {
                    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
                    'вычищено: ' + $dir
                }
            }
            foreach ($dir in @({{recipeDirs}})) {
                if (Test-Path $dir) {
                    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
                    'вычищено: ' + $dir
                }
            }
            if (Test-Path '{{CloudFallbackToolsDir}}') {
                Remove-Item '{{CloudFallbackToolsDir}}' -Recurse -Force -ErrorAction SilentlyContinue
                'вычищено: {{CloudFallbackToolsDir}}'
            }
            # tools\ рядом с агентом (обычный случай) — раньше уборка про него не знала
            # вовсе, и prime95/lhmmon (101 МБ) оставались на клиенте навсегда (бэклог п.158).
            $pp = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction SilentlyContinue).ParentProcessId
            $agent = (Get-Process -Id $pp -ErrorAction SilentlyContinue).Path
            if ($agent -and (Split-Path $agent -Leaf) -eq 'agent.exe') {
                $agentToolsDir = Join-Path (Split-Path $agent) 'tools'
                if (Test-Path $agentToolsDir) {
                    Remove-Item $agentToolsDir -Recurse -Force -ErrorAction SilentlyContinue
                    'вычищено: ' + $agentToolsDir
                }
            }
            'cleanup-done'
            """;
    }

    /// <summary>«Снять ВСЮ нагрузку одной командой» — процессы стресс-тулов и `lhmmon`,
    /// фоновые `exec --detach`-задачи, что гоняют наши скрипты, задачи планировщика и
    /// драйверы инструментов. НЕ трогает временные каталоги — это забота `client cleanup`/
    /// `wipe-tools`, которым здесь освобождается путь (файлы больше не заняты).
    ///
    /// Боль (бэклог п.126, СЗ 161346): рецепт задавил канал управления, `exec` не проходил,
    /// а оператор успел остановить только OCCT руками — фоновая дисковая нагрузка продолжала
    /// давить машину ещё 180 минут незамеченной. Боль (бэклог п.183): `stop-stress.ps1`
    /// сознательно не трогал `lhmmon`, поэтому его процесс/задача/драйвер `R0lhmmon`
    /// переживали «остановку», и `wipe-tools` спотыкался об занятую папку.</summary>
    /// <param name="keepTasks">Задачи текущей сессии (sshd/watchdog/автостарт) — не трогать.</param>
    public static string BuildStressStopScript(IReadOnlyList<string>? keepTasks = null)
    {
        var names = string.Join(",", StressProcessNames.Select(n => $"'{n}'"));
        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $killed = @()
            foreach ($n in @({{names}})) {
                $p = Get-Process $n -ErrorAction SilentlyContinue
                if ($p) { $p | Stop-Process -Force -ErrorAction SilentlyContinue; $killed += "$n x$($p.Count)" }
            }
            $jobs = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandLine -and $_.CommandLine -match 'szdiag\\jobs' })
            foreach ($j in $jobs) {
                Stop-Process -Id $j.ProcessId -Force -ErrorAction SilentlyContinue
                $killed += "job pid=$($j.ProcessId)"
            }
            if ($killed.Count -eq 0) { 'процессов не было — снимать нечего' } else { 'снято: ' + ($killed -join ', ') }
            {{TasksAndDriversScript(keepTasks)}}
            'stress-stop-done'
            """;
    }

    /// <summary>Разбор вывода инвентаря в список проблем. Пустой список — следов нет.
    /// Плоский вариант без знания текущей СЗ — всё считается остатками.</summary>
    public static IReadOnlyList<string> FindLeftovers(string inventoryStdout)
        => FindLeftoversDetailed(inventoryStdout, sz: null).Leftovers;

    /// <summary>Фактический путь к логу агента из вывода инвентаря (строка `log:`);
    /// null — агент не определился (exec шёл не из-под agent.exe).</summary>
    public static string? AgentLogPath(string inventoryStdout)
        => (inventoryStdout ?? "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("log:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["log:".Length..].Trim())
            .FirstOrDefault(p => p.Length > 0);

    /// <summary>Состояние perf-счётчиков клиента из строки `perf:` вывода инвентаря. Null —
    /// счётчики живы (или строки нет вовсе — старый агент); непустая строка — сообщение об
    /// ошибке (`Invalid class` и т.п.), которое ловится этой пробой (бэклог п.201): рецепты
    /// на такой машине молча отчитывались успехом с пустой таблицей вместо явного отказа.</summary>
    public static string? PerfCountersBroken(string inventoryStdout)
    {
        var line = (inventoryStdout ?? "").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("perf:", StringComparison.OrdinalIgnoreCase));
        if (line is null) return null;
        var value = line["perf:".Length..].Trim();
        return value.StartsWith("broken=", StringComparison.OrdinalIgnoreCase)
            ? value["broken=".Length..].Trim()
            : null;
    }

    /// <summary>Фактический каталог тулов рядом с агентом (строка `agenttoolsdir:`) — раньше
    /// его можно было узнать только по `appsettings.json`/`ToolsDirectory.Resolve` на хосте
    /// вслепую (бэклог п.151): облачный агент (OneDrive) молча уводил раздачу в ProgramData.</summary>
    public static string? ToolsDirFromInventory(string inventoryStdout)
        => (inventoryStdout ?? "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("agenttoolsdir:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["agenttoolsdir:".Length..].Trim())
            .FirstOrDefault(p => p.Length > 0);

    /// <summary>Задачи рабочего доступа текущей сессии по её номеру СЗ.</summary>
    public static string[] SessionTasks(string sz)
        => new[] { $"szdiag-sshd-{sz}", $"szdiag-watchdog-{sz}", $"szdiag-autostart-{sz}" };

    /// <summary>Разбор инвентаря с разделением на «текущая сессия (не трогать)» и «остатки».
    /// Без этого `client info` сразу после подъёма агента называл рабочий sshd/watchdog
    /// «остатками» и советовал cleanup — снести себе доступ посреди заявки (бэклог п.107).</summary>
    public static TraceReport FindLeftoversDetailed(string inventoryStdout, string? sz)
    {
        var session = sz is null ? Array.Empty<string>() : SessionTasks(sz);
        var current = new List<string>();
        var leftovers = new List<string>();

        foreach (var raw in (inventoryStdout ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var sep = line.IndexOf('=');
            if (sep <= 0) continue;
            var key = line[..sep];
            var value = line[(sep + 1)..].Trim();

            if (key.StartsWith("task:", StringComparison.OrdinalIgnoreCase))
            {
                var name = key["task:".Length..];
                if (session.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    current.Add($"задача {name}: {value}");
                    continue;
                }
                var orphan = !name.Any(char.IsDigit) ? " (без номера СЗ — чей хвост, неизвестно)" : "";
                leftovers.Add($"задача {name}: {value}{orphan}");
            }
            else if (key.StartsWith("service:", StringComparison.OrdinalIgnoreCase)
                     && !value.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                leftovers.Add($"драйвер {key["service:".Length..]}: {value}");
            }
            else if (key.StartsWith("big:", StringComparison.OrdinalIgnoreCase))
            {
                leftovers.Add($"крупный файл {key["big:".Length..]}: {value} ГБ");
            }
            else if (key.StartsWith("dir:", StringComparison.OrdinalIgnoreCase)
                     && !value.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                // Раньше эти строки печатались скриптом, но парсер их не читал вовсе —
                // client info молчал именно там, где остатки реально лежали (бэклог п.158).
                leftovers.Add($"каталог {key["dir:".Length..]}: {value} МБ");
            }
            else if (key.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
            {
                leftovers.Add($"доставленный инструмент {key["tool:".Length..]}: {value} МБ");
            }
        }
        return new TraceReport(current, leftovers);
    }
}

/// <summary>Итог осмотра клиента: рабочий доступ текущей сессии отдельно от остатков.</summary>
public sealed record TraceReport(IReadOnlyList<string> CurrentSession, IReadOnlyList<string> Leftovers);
