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

    private sealed record SensorRun(string JobId, string CsvPath, DateTimeOffset StartedAt);

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
            JsonSerializer.Serialize(new SensorRun(res.JobId, csvPath, DateTimeOffset.Now)));

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

        try
        {
            var status = await client.ExecStatusAsync(sz, run.JobId, 20);
            if (status is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
                return 1;
            }
            var state = status.Running ? "[yellow]идёт[/]" : $"[grey]завершён[/] (exit {status.ExitCode})";
            AnsiConsole.MarkupLineInterpolated($"Наблюдатель {run.JobId}: {state}, CSV {run.CsvPath}");
            if (!string.IsNullOrEmpty(status.Error)) AnsiConsole.MarkupLineInterpolated($"[red]{status.Error}[/]");
            return 0;
        }
        catch (TimeoutException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Таймаут:[/] {ex.Message}");
            return 1;
        }
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

        var summary = SensorReport.Summarize(SensorReport.Parse(File.ReadAllText(csvPath)));
        Console.WriteLine(SensorReport.Format(summary));
        return summary.Samples == 0 ? 1 : 0;
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
