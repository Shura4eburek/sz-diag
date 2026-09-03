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
        return 0;
    }

    private static async Task<int> StatusAsync(IHubApiClient client, string sz, string stateDir, bool tailOnly)
    {
        var run = Load(stateDir, sz);
        if (run is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]По СЗ {sz} наблюдатель не запускался[/] (нет состояния на хосте).");
            return 1;
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
            AnsiConsole.MarkupLineInterpolated($"[yellow]По СЗ {sz} наблюдатель не запускался.[/]");
            return 1;
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
        AnsiConsole.MarkupLineInterpolated($"[grey]Забрать CSV:[/] szcli pull {sz} \"{run.CsvPath}\"");
        return 0;
    }

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
