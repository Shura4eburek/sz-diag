using System.Linq;
using System.Text.Json;
using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli sensors start|status|stop|report` — снятие и разбор наблюдений во время
/// стресс-прогона.
///
/// Ритуал повторялся на каждой заявке вручную: доставить логгер → написать run.cmd → создать
/// задачу под SYSTEM → запустить → не забыть остановить → забрать CSV → **каждый раз заново
/// писать парсер** для max/avg (бэклог п.7). А главное — вопрос «сколько времени нагрузка
/// реально держалась» без пересчёта по CSV отвечался неверно.
///
/// Наблюдатель запускается **фоновой exec-задачей** (`--detach`), потому что под полной
/// нагрузкой синхронный exec не проходит вообще (п.43), и пишет CSV построчно, чтобы
/// пережить вырубон (п.64).</summary>
public static class SensorsCommand
{
    /// <summary>Процессы стресс-тулов, наличие которых считаем признаком идущего прогона.</summary>
    private static readonly string[] StressProcesses =
        { "OCCT", "OCCTCmd", "OCCTEnterprise", "TM5", "FurMark", "furmark", "prime95", "3DMark" };

    private const int DefaultIntervalSeconds = 10;
    private const int DefaultMinutes = 240;

    private static string StatePath(string stateDir, string sz)
        => Path.Combine(stateDir, "sensors", $"{sz}.json");

    private sealed record SensorRun(string JobId, string CsvPath, DateTimeOffset StartedAt,
        int IntervalSeconds = DefaultIntervalSeconds);

    public static async Task<int> RunAsync(IHubApiClient client, string[] args, string stateDir)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (sub is "start" or "status" or "stop" or "pull" && args.Length < 2)
        {
            AnsiConsole.MarkupLine("[red]Не указан номер СЗ.[/]");
            return 2;
        }

        return sub switch
        {
            "start" => await StartAsync(client, args, stateDir),
            "status" => await StatusAsync(client, args[1], stateDir, tailOnly: false),
            "stop" => await StopAsync(client, args[1], stateDir),
            "report" when args.Length >= 2 => Report(args[1]),
            _ => Usage(),
        };
    }

    private static async Task<int> StartAsync(IHubApiClient client, string[] args, string stateDir)
    {
        var sz = args[1];
        var minutes = ArgValue(args, "--minutes") is { } m && int.TryParse(m, out var mm) ? mm : DefaultMinutes;
        var interval = ArgValue(args, "--interval") is { } i && int.TryParse(i, out var ii) ? ii : DefaultIntervalSeconds;

        // Прошлый прогон этой же СЗ мог не быть остановлен явно: CSV не потеряется (у каждого
        // прогона свой файл с таймстампом в имени — второй физически не может затереть первый),
        // но состояние на хосте (`sensors/<sz>.json`) сейчас перезатирается молча, и CLI теряет
        // из виду job предыдущего прогона (его process на клиенте продолжает писать свой CSV
        // и жрать ресурсы, а `sensors stop`/`status` его больше не видят) — бэклог п.145.
        var previous = Load(stateDir, sz);
        if (PreviousRunWarning(previous?.JobId, previous?.CsvPath, previous?.StartedAt) is { } warning)
            AnsiConsole.MarkupLineInterpolated($"[yellow]⚠ {warning}[/]");

        // Пишем в ProgramData, а не рядом с агентом: папка агента может оказаться внутри
        // OneDrive клиента (п.63), а CSV прогона туда уезжать не должен.
        var csvPath = $@"C:\ProgramData\szdiag\sensors\{sz}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        var script = "New-Item -ItemType Directory -Force -Path (Split-Path '" + csvPath + "') | Out-Null\n"
                     + SensorWatcher.BuildScript(csvPath, interval, minutes, StressProcesses);

        var res = await client.ExecAsync(sz, script, 60, default, detached: true);
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }
        if (res.JobId is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Не удалось поднять наблюдатель:[/] {res.StdErr}");
            return 1;
        }

        var path = StatePath(stateDir, sz);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(new SensorRun(res.JobId, csvPath, DateTimeOffset.Now, interval)));

        AnsiConsole.MarkupLineInterpolated(
            $"[green]СЗ {sz}: наблюдатель запущен[/] (job {res.JobId}, интервал {interval} с, до {minutes} мин)");
        AnsiConsole.MarkupLineInterpolated($"[grey]CSV на клиенте:[/] {csvPath}");
        AnsiConsole.MarkupLine("[grey]Проверить:[/] szcli sensors status <СЗ>   [grey]забрать:[/] szcli pull <СЗ> <csv>");
        // Бэклог п.153: cpu_temp_c читается из ACPI thermal zone и на многих машинах пуст —
        // «CSV пишется, GPU-колонки живые» выглядит как рабочий прогон, хотя вопрос «перегрев
        // или нет» этот источник не закрывает вовсе. Предупреждаем сразу, а не постфактум
        // в `sensors report`, когда прогон уже потрачен.
        AnsiConsole.MarkupLine("[grey]Если cpu_temp_c в CSV окажется пустой (нет датчика ACPI) — подними lhmmon:[/] tools/recipes/client/start-sensors.ps1");
        return 0;
    }

    private static async Task<int> StatusAsync(IHubApiClient client, string sz, string stateDir, bool tailOnly)
    {
        var run = Load(stateDir, sz);
        if (run is null)
        {
            // Наблюдатель мог быть поднят не командой, а рецептом (`start-sensors.ps1` —
            // задача `szdiag-lhm-<СЗ>` под SYSTEM, lhmmon пишет своё CSV): hub про такой job
            // ничего не знает, но факт на клиенте есть (бэклог п.190, СЗ 160705 — `stop` ответил
            // «наблюдатель не запускался» на живом захвате). Смотрим на клиента напрямую.
            var probe = await ProbeClientAsync(client, sz);
            if (probe is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
                return 1;
            }
            if (!ProbeFoundWatcher(probe))
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]По СЗ {sz} наблюдатель не запускался[/] (нет ни состояния на хосте, ни следов на клиенте).");
                return 1;
            }
            AnsiConsole.MarkupLine($"[grey]Наблюдатель поднят не этой командой[/] (состояния на хосте нет) — " +
                $"смотрю на клиента напрямую: {Markup.Escape(FormatProbe(probe, DateTimeOffset.Now))}");
            return 0;
        }

        // Сетевые сбои не ловим здесь: их разбирает общий обработчик CLI (CliErrors, п.70/78) —
        // одна строка вместо стектрейса, одинаковая для всех подкоманд.
        var status = await client.ExecStatusAsync(sz, run.JobId, 20);
        if (status is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }
        // Свежесть по факту, а не по «процесс жив»: под нагрузкой наблюдатель может висеть на
        // WMI минутами, оставаясь формально запущенным (бэклог п.206, СЗ 161716, дыра 18 минут
        // при status == «идёт»). LastOutputAt — файл-таймстамп stdout фоновой задачи, тот же
        // канал, что уже проверен под полной нагрузкой (п.208); хартбит наблюдателя
        // (см. SensorWatcher) держит его свежим, пока идёт цикл, и несёт номер строки CSV.
        var rows = ParseHeartbeatRows(status.Tail);
        var stale = IsStale(status.Running, status.LastOutputAt, run.IntervalSeconds, DateTimeOffset.Now);
        var state = stale
            ? $"[red]не пишет {StaleMinutes(status.LastOutputAt, DateTimeOffset.Now):N0} мин[/]"
            : status.Running ? "[yellow]идёт[/]" : $"[grey]завершён[/] (exit {status.ExitCode})";
        // MarkupLine + Escape для данных: Interpolated-вариант съедал разметку из $state и
        // печатал её текстом («[yellow]идёт[/]» на 260306).
        AnsiConsole.MarkupLine($"Наблюдатель {Markup.Escape(run.JobId)}: {state}, CSV {Markup.Escape(run.CsvPath)}");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(FreshnessLine(rows, status.LastOutputAt, DateTimeOffset.Now))}[/]");
        if (!string.IsNullOrEmpty(status.Error)) AnsiConsole.MarkupLineInterpolated($"[red]{status.Error}[/]");
        return 0;
    }

    private static async Task<int> StopAsync(IHubApiClient client, string sz, string stateDir)
    {
        var run = Load(stateDir, sz);
        if (run is null)
        {
            // Тот же фолбэк, что и в status: наблюдатель мог поднять рецепт (бэклог п.190).
            var probe = await ProbeClientAsync(client, sz);
            if (probe is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
                return 1;
            }
            if (!ProbeFoundWatcher(probe))
            {
                AnsiConsole.MarkupLineInterpolated($"[yellow]По СЗ {sz} наблюдатель не запускался.[/]");
                return 1;
            }

            var stopRecipe = await client.ExecAsync(sz, StopLhmScript(sz), 30, default, detached: false);
            if (stopRecipe is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
                return 1;
            }
            AnsiConsole.MarkupLineInterpolated(
                $"[green]СЗ {sz}: наблюдатель (задача {LhmTaskName(sz)}) остановлен.[/]");
            return 0;
        }

        // Наблюдатель — обычный PowerShell-цикл; глушим по имени файла его скрипта.
        var script = $"Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | " +
                     $"Where-Object {{ $_.CommandLine -like '*{run.JobId}*' }} | " +
                     "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; 'stopped'";
        var res = await client.ExecAsync(sz, script, 120, default, detached: false);
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }

        File.Delete(StatePath(stateDir, sz));
        AnsiConsole.MarkupLineInterpolated($"[green]СЗ {sz}: наблюдатель остановлен.[/]");

        // Бэклог п.7: раньше CSV нужно было забирать руками и заново писать разбор на каждой
        // заявке. Теперь `stop` сам подтягивает файл и кладёт готовую сводку в журнал СЗ —
        // «была ли нагрузка настоящей» видно без отдельного ритуала.
        await AutoCollectAsync(client, sz, run.CsvPath);
        return 0;
    }

    /// <summary>Забрать CSV на хост, разобрать и положить сводку в журнал СЗ. Неудача здесь —
    /// не критична (наблюдатель уже остановлен, CSV на клиенте цел) — оператор заберёт руками.</summary>
    private static async Task AutoCollectAsync(IHubApiClient client, string sz, string csvPath)
    {
        try
        {
            var pulled = await client.PullAsync(sz, csvPath);
            var saved = pulled?.Files.FirstOrDefault(f => f.SavedPath is not null)?.SavedPath;
            if (saved is null)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]CSV не забрался автоматически — забери руками:[/] szcli pull {sz} \"{csvPath}\"");
                return;
            }

            var parsed = SensorReport.ParseAny(await File.ReadAllTextAsync(saved));
            var parsedSummary = SensorReport.Summarize(parsed.Samples, format: parsed.Format);
            var summary = SensorReport.Format(parsedSummary);
            AnsiConsole.MarkupLineInterpolated($"[grey]CSV забран:[/] {saved}");
            Console.WriteLine(summary);

            // Журнал СЗ — на украинском (kb, CLAUDE.md); консольный Format выше остаётся
            // русским — это два разных читателя одного и того же SensorSummary (review W2 I-6).
            var summaryUa = SensorReport.FormatForJournal(parsedSummary);
            await client.AddNoteAsync(sz, BuildJournalNote(Path.GetFileName(csvPath), summaryUa));
            AnsiConsole.MarkupLine("[grey]Сводка добавлена в журнал СЗ.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]Автозабор CSV не удался ({ex.Message}) — забери руками:[/] szcli pull {sz} \"{csvPath}\"");
        }
    }

    /// <summary>Текст записи в журнал СЗ по итогам прогона сенсоров — чистая функция ради тестов.
    /// Журнал СЗ на украинском (kb, CLAUDE.md) — обёртка и сам текст сводки (`summaryText`
    /// ожидается уже переведённым, см. <see cref="SensorReport.FormatForJournal"/>): раньше сюда
    /// уезжал русский текст без перевода (review W2 I-6).</summary>
    public static string BuildJournalNote(string csvFileName, string summaryText)
        => $"Зведення сенсорів ({csvFileName}):\n{summaryText}";

    /// <summary>Разбор забранного CSV — считает, сколько времени нагрузка реально держалась.</summary>
    private static int Report(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Файл не найден:[/] {csvPath}");
            return 1;
        }

        // Формат определяем сами: широкий лог lhmmon раньше давал «CSV пуст — прогон не
        // подтверждён ничем» на полностью валидном пятичасовом файле (бэклог п.91).
        var parsed = SensorReport.ParseAny(File.ReadAllText(csvPath));
        var summary = SensorReport.Summarize(parsed.Samples, format: parsed.Format);
        Console.WriteLine(SensorReport.Format(summary));
        return summary.Samples == 0 ? 1 : 0;
    }

    /// <summary>Текст предупреждения о незакрытом прошлом прогоне — null, если прошлого
    /// прогона не было. Чистая функция без побочных эффектов — тестируется без файловой
    /// системы и сети (бэклог п.145).</summary>
    public static string? PreviousRunWarning(string? previousJobId, string? previousCsvPath,
        DateTimeOffset? previousStartedAt)
    {
        if (previousJobId is null) return null;
        return $"Прошлый прогон не остановлен явно: job {previousJobId}, CSV {previousCsvPath} " +
               $"(запущен {previousStartedAt:dd.MM HH:mm}). Новые данные пишутся в отдельный файл — " +
               "старые не тронуты, но процесс на клиенте мог продолжать работать: szcli exec <СЗ> --jobs";
    }

    /// <summary>Номер строки CSV из последнего хартбита наблюдателя в хвосте вывода фоновой
    /// задачи (<c>tick;&lt;номер строки&gt;;&lt;время&gt;</c> — см. <see cref="SensorWatcher"/>).
    /// Время берём не отсюда, а из <see cref="ExecJobStatus.LastOutputAt"/> (файл-таймстамп,
    /// уже DateTimeOffset и уже проверен под нагрузкой — п.208) — здесь нужно только «сколько
    /// строк». Чистая функция без сети/файлов — тестируется напрямую (бэклог п.206).</summary>
    public static int? ParseHeartbeatRows(string? tail)
    {
        if (string.IsNullOrWhiteSpace(tail)) return null;

        string? last = null;
        foreach (var raw in tail.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("tick;", StringComparison.OrdinalIgnoreCase)) last = line;
        }
        if (last is null) return null;

        var parts = last.Split(';');
        return parts.Length >= 2 && int.TryParse(parts[1], out var r) ? r : null;
    }

    /// <summary>Наблюдатель формально «идёт» (процесс жив), но не дописал ни строки за
    /// &gt; 3 интервала — тот самый сценарий 18-минутной дыры на 161716, когда `status` врал
    /// «идёт» весь простой.</summary>
    public static bool IsStale(bool running, DateTimeOffset? lastOutputAt, int intervalSeconds, DateTimeOffset now)
    {
        if (!running || lastOutputAt is null) return false;
        return StaleMinutes(lastOutputAt, now) > intervalSeconds * 3.0 / 60.0;
    }

    private static double StaleMinutes(DateTimeOffset? lastOutputAt, DateTimeOffset now)
        => lastOutputAt is null ? 0 : (now - lastOutputAt.Value).TotalMinutes;

    /// <summary>Строка «сколько строк / когда последняя» — то, ради чего раньше приходилось
    /// делать `szcli pull` дважды и сравнивать `wc -l` руками.</summary>
    public static string FreshnessLine(int? rows, DateTimeOffset? lastAt, DateTimeOffset now)
    {
        if (lastAt is null) return "свежесть неизвестна: хартбит от наблюдателя ещё не пришёл";
        var rowsText = rows is { } n ? $"{n} строк" : "число строк неизвестно";
        return $"{rowsText}, последняя {lastAt:HH:mm:ss} ({StaleMinutes(lastAt, now):N1} мин назад)";
    }

    /// <summary>Имя задачи, которую заводит рецепт `start-sensors.ps1` (lhmmon под SYSTEM) —
    /// единая точка, чтобы имя не разъезжалось между C# и `.ps1` (бэклог п.190).</summary>
    private static string LhmTaskName(string sz) => $"szdiag-lhm-{sz}";

    /// <summary>CSV, в который лог lhmmon пишет по факту у рецепта (`start-sensors.ps1`).</summary>
    private const string LhmCsvPath = @"C:\OCCT\sensors.csv";

    /// <summary>Факт наличия наблюдателя на клиенте — независимо от того, кто его поднял:
    /// команда `sensors start` или рецепт `start-sensors.ps1` (бэклог п.190, СЗ 160705).</summary>
    public sealed record ClientSensorProbe(bool ProcessAlive, string? TaskState, bool CsvExists,
        int? Rows, DateTimeOffset? LastWrite);

    /// <summary>Скрипт-разведка: задача `szdiag-lhm-<СЗ>`, процесс `lhmmon`, CSV лога. Чистый
    /// синхронный exec — под полной нагрузкой может не пройти (как любой ad-hoc exec), но это
    /// уже лучше, чем гарантированное «наблюдатель не запускался».</summary>
    private static string ProbeScript(string sz)
    {
        var task = LhmTaskName(sz).Replace("'", "''");
        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $taskState = $null
            $q = schtasks /query /tn '{{task}}' /fo LIST 2>$null
            if ($q) {
                $line = $q | Select-String '^Status:'
                if ($line) { $taskState = ($line.Line -replace '^Status:\s*','').Trim() }
            }
            $proc = Get-Process lhmmon -ErrorAction SilentlyContinue
            $csv = '{{LhmCsvPath}}'
            $exists = Test-Path $csv
            $rows = $null; $last = $null
            if ($exists) {
                $rows = (Get-Content $csv | Measure-Object -Line).Lines
                $last = (Get-Item $csv).LastWriteTime.ToString('o')
            }
            [PSCustomObject]@{
                ProcessAlive = [bool]$proc
                TaskState    = $taskState
                CsvExists    = $exists
                Rows         = $rows
                LastWrite    = $last
            } | ConvertTo-Json -Compress
            """;
    }

    /// <summary>Останавливает наблюдателя, поднятого рецептом: гасит процесс и снимает задачу.</summary>
    private static string StopLhmScript(string sz)
    {
        var task = LhmTaskName(sz).Replace("'", "''");
        return $"Stop-Process -Name lhmmon -Force -ErrorAction SilentlyContinue; " +
               $"schtasks /end /tn '{task}' 2>$null | Out-Null; " +
               $"schtasks /delete /tn '{task}' /f 2>$null | Out-Null; 'stopped'";
    }

    private static async Task<ClientSensorProbe?> ProbeClientAsync(IHubApiClient client, string sz)
    {
        var res = await client.ExecAsync(sz, ProbeScript(sz), 20, default, detached: false);
        return res is null ? null : ParseProbe(res.StdOut);
    }

    /// <summary>Разбор JSON-ответа разведки. Чистая функция — тестируется без сети (бэклог п.190).</summary>
    public static ClientSensorProbe? ParseProbe(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        // ConvertTo-Json — последняя непустая строка вывода: PowerShell мог что-то ворчнуть
        // раньше (schtasks на нелокализованной консоли, например).
        var json = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<ClientSensorProbe>(json); }
        catch { return null; }
    }

    /// <summary>Есть ли живой наблюдатель по фактам разведки — задача существует/запущена,
    /// процесс жив или CSV на месте. Пустой список фактов = «не запускался» ни от кого.</summary>
    public static bool ProbeFoundWatcher(ClientSensorProbe probe)
        => probe.ProcessAlive || probe.CsvExists || !string.IsNullOrEmpty(probe.TaskState);

    /// <summary>Строка для человека: задача/процесс/CSV одним взглядом.</summary>
    public static string FormatProbe(ClientSensorProbe probe, DateTimeOffset now)
    {
        var task = probe.TaskState ?? "задача не найдена";
        var proc = probe.ProcessAlive ? "процесс жив" : "процесс не найден";
        var csv = probe.CsvExists
            ? $"CSV {(probe.Rows is { } n ? $"{n} строк" : "есть")}" +
              (probe.LastWrite is { } w ? $", последняя запись {StaleMinutes(w, now):N1} мин назад" : "")
            : "CSV не создан";
        return $"задача: {task}; {proc}; {csv}";
    }

    private static SensorRun? Load(string stateDir, string sz)
    {
        var path = StatePath(stateDir, sz);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SensorRun>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var idx = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && args.Length > idx + 1 ? args[idx + 1] : null;
    }

    private static int Usage()
    {
        AnsiConsole.MarkupLine("""
            Использование:
              szcli sensors start <СЗ> [[--interval <сек>]] [[--minutes <мин>]]   поднять наблюдатель
              szcli sensors status <СЗ>                                      идёт ли он
              szcli sensors stop <СЗ>                                        остановить
              szcli sensors report <csv-на-хосте>                            разбор: сколько реально шла нагрузка
            """);
        return 2;
    }
}
