using System.Text;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using SzDiag.Cli;
using SzDiag.Contracts;
using SzDiag.Kb;

// UTF-8 в консоли — иначе кириллица и рамки таблицы ломаются на Windows.
try { Console.OutputEncoding = Encoding.UTF8; } catch { /* вывод может быть перенаправлен — не критично */ }

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("SZDIAG_")
    .Build();

var options = new CliOptions();
config.Bind(options);

using var http = new HttpClient { BaseAddress = new Uri(options.HubBaseUrl) };
var client = new HubApiClient(http, options.ManagementToken);

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "watch";

// Справка — до всякого разбора аргументов: `--help` не должен уехать куда-то номером СЗ.
if (command is "--help" or "-h" or "help" or "/?")
{
    PrintUsage();
    return 0;
}

// Номер СЗ проверяем один раз на входе: команды, которые его принимают, перечислены явно.
// Мусорный ввод раньше молча уезжал в hub и в базу знаний (бэклог п.57).
var szArgIndex = command switch
{
    "close" or "target" or "exec" or "pull" or "reboots" or "freeze" or "unfreeze" or "note"
        when args.Length >= 2 => 1,
    "push" when args.Length >= 2 && !args[1].StartsWith('-') => 1,
    "test" or "diag" when args.Length >= 3 => 2,
    _ => -1
};
if (szArgIndex > 0 && !SzNumber.IsValid(args[szArgIndex]))
{
    AnsiConsole.MarkupLineInterpolated($"[red]Неверный номер СЗ:[/] {SzNumber.Explain(args[szArgIndex])}.");
    return 2;
}

// Сетевые сбои разбираются один раз на весь CLI: раньше обработка была точечной, и каждая
// новая команда приезжала без неё — `sensors status` под нагрузкой выдавал 25 строк
// TaskCanceledException вместо строки «агент не ответил» (бэклог п.70/78).
try
{
switch (command)
{
    case "list":
        AnsiConsole.Write(SessionTableRenderer.Render(await client.GetSessionsAsync()));
        break;

    case "watch":
        await WatchAsync(client);
        break;

    case "close" when args.Length >= 2:
        if (await client.CloseAsync(args[1]))
        {
            AnsiConsole.MarkupLineInterpolated($"[green]СЗ {args[1]} закрыта[/] (revert отправлен агенту).");
            // Сводка по вырубонам при закрытии — чтобы вердикт «не воспроизвели» нельзя было
            // написать не глядя: на 160306 машина вырубилась у нас на стенде, и это заметили
            // через неделю (бэклог п.55).
            await PrintRebootSummaryAsync(client, args[1]);
            // Заморозка обязана сниматься до отдачи машины клиенту — иначе она уедет
            // без обновлений безопасности (бэклог п.34b).
            FreezeCommand.WarnIfStillFrozen(AppContext.BaseDirectory, args[1]);
            // Следы прогонов агент чистит сам при откате, но 12 ГБ iotest.bin из папки
            // клиента он не тронет — проверять надо ДО закрытия (бэклог п.56/99).
            AnsiConsole.MarkupLineInterpolated(
                $"[grey]Проверить остатки на клиенте (пока агент жив):[/] szcli client info {args[1]}");
            // Забытая метка обслуживания скроет реальный дефект — та же ловушка, что с unfreeze.
            await MaintenanceCommand.WarnIfActiveAsync(client, args[1]);
        }
        else
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {args[1]} не найдена[/] среди активных.");
        break;

    // agent restart <СЗ>: поднять агента заново, не подходя к машине. Агент себя НЕ убивает —
    // он ставит отложенную задачу под SYSTEM, и только она гасит процесс и запускает новый
    // (прошлая попытка сделать это скриптом стоила потери машины — бэклог п.83).
    case "agent" when args.Length >= 3 && args[1].Equals("restart", StringComparison.OrdinalIgnoreCase):
    {
        var restartSz = args[2];
        if (!SzNumber.IsValid(restartSz))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Неверный номер СЗ:[/] {SzNumber.Explain(restartSz)}.");
            return 2;
        }

        var restart = await client.ExecAsync(restartSz, AgentRestart.BuildScript(restartSz), 120);
        if (restart is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {restartSz} не найдена[/] среди активных.");
            return 1;
        }
        if (!string.IsNullOrEmpty(restart.StdOut)) Console.WriteLine(CliXml.Decode(restart.StdOut).TrimEnd());
        if (restart.ExitCode != 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Перезапуск не поставлен:[/] {CliXml.Decode(restart.StdErr).TrimEnd()}");
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated(
            $"[green]СЗ {restartSz}: перезапуск поставлен.[/] Через минуту СЗ должна вернуться в [green]online[/] — следи в szcli watch.");
        break;
    }

    // maintenance: метка «с машиной работают руками» — событие питания в этом окне не
    // дефект, а вечернее выключение стенда (бэклог п.100).
    case "maintenance" when args.Length >= 2:
        return await MaintenanceCommand.RunAsync(client, args);

    // client info|cleanup: следы прогонов на клиентской машине (задачи szdiag*, драйверы
    // инструментов, наши временные каталоги) — бэклог п.56/88/99.
    case "client" when args.Length >= 2:
        return await ClientCommand.RunAsync(client, args);

    // agent set <СЗ> Ключ=значение: правка конфига агента с хоста. WatchdogHours применяется
    // сразу (перевзвод задачи), остальное — при следующем открытии доступа (бэклог п.86).
    case "agent" when args.Length >= 4 && args[1].Equals("set", StringComparison.OrdinalIgnoreCase):
    {
        var setSz = args[2];
        if (!SzNumber.IsValid(setSz))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Неверный номер СЗ:[/] {SzNumber.Explain(setSz)}.");
            return 2;
        }
        var assignment = AgentConfigEdit.ParseAssignment(args[3]);
        if (assignment is null)
        {
            AnsiConsole.MarkupLine("[red]Формат:[/] szcli agent set <СЗ> WatchdogHours=12");
            return 2;
        }
        if (!AgentConfigEdit.IsKnownKey(assignment.Value.Key))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Неизвестный ключ:[/] {assignment.Value.Key}");
            AnsiConsole.MarkupLineInterpolated(
                $"[grey]Применяются сразу:[/] {string.Join(", ", AgentConfigEdit.HotKeys)}");
            AnsiConsole.MarkupLineInterpolated(
                $"[grey]При следующем открытии:[/] {string.Join(", ", AgentConfigEdit.RestartKeys)}");
            return 2;
        }

        var setRes = await client.ExecAsync(setSz,
            AgentConfigEdit.BuildScript(setSz, assignment.Value.Key, assignment.Value.Value), 120);
        if (setRes is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {setSz} не найдена[/] среди активных.");
            return 1;
        }
        if (!string.IsNullOrEmpty(setRes.StdOut)) Console.WriteLine(CliXml.Decode(setRes.StdOut).TrimEnd());
        if (setRes.ExitCode != 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Не удалось:[/] {CliXml.Decode(setRes.StdErr).TrimEnd()}");
            return 1;
        }
        break;
    }

    // sensors: наблюдатель нагрузки на время стресс-прогона + разбор его CSV.
    case "sensors" when args.Length >= 2:
        return await SensorsCommand.RunAsync(client, args[1..], AppContext.BaseDirectory);

    // freeze/unfreeze: заморозка Windows Update на время сессии. Прежние значения хранятся
    // на хосте рядом с szcli — клиент их потерять не может.
    // freeze --status <СЗ>: держится ли заморозка (после ребута она сама не переживает —
    // бэклог п.72), без ручного exec в реестр.
    case "freeze" when args.Length >= 3 && args[2].Equals("--status", StringComparison.OrdinalIgnoreCase):
        return await FreezeCommand.StatusAsync(client, args[1], AppContext.BaseDirectory);

    case "freeze" when args.Length >= 2:
        return await FreezeCommand.FreezeAsync(client, args[1], AppContext.BaseDirectory);

    case "unfreeze" when args.Length >= 2:
        return await FreezeCommand.UnfreezeAsync(client, args[1], AppContext.BaseDirectory);

    // reboots: таймлайн вырубонов по СЗ (по сменам boot-time, а не по пропаже heartbeat).
    // Ручной шаг у машины (свап железа, правка BIOS, осмотр). Пишется в журнал СЗ сразу:
    // до конца дня оно забывается — на 160697 так пропал свап БП вместе с результатом прогона.
    case "note" when args.Length >= 3:
    {
        // Номер уже проверен на входе (общий блок валидации выше).
        var noteSz = args[1];

        // Кавычки вокруг текста необязательны: всё, что после номера, — одна заметка.
        var noteText = string.Join(' ', args[2..]);
        if (await client.AddNoteAsync(noteSz, noteText))
            AnsiConsole.MarkupLineInterpolated($"[green]СЗ {noteSz}: записано в журнал[/]");
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {noteSz}: hub не принял заметку[/]");
            return 1;
        }
        break;
    }

    case "reboots" when args.Length >= 2:
    {
        var timeline = await client.GetRebootsAsync(args[1]);
        if (timeline is null || timeline.Count == 0)
        {
            // «Не зафиксировано» ≠ «не было»: важно, с какого момента машина под наблюдением
            // (бэклог п.97).
            var sinceText = timeline?.WatchingSince is { } w
                ? $"под наблюдением с {w.ToLocalTime():dd.MM HH:mm}"
                : "под наблюдением ещё не была";
            AnsiConsole.MarkupLineInterpolated(
                $"[grey]СЗ {args[1]}: вырубонов не зафиксировано[/] ({sinceText}; журнал клиента агент приносит при подключении).");
            break;
        }

        var rebootTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        rebootTable.AddColumn("Когда");
        rebootTable.AddColumn("Как");
        rebootTable.AddColumn("Откуда");
        rebootTable.AddColumn("Продержалась");
        rebootTable.AddColumn("Была занята");
        foreach (var e in timeline.Events)
        {
            var held = e.UptimeBefore is { } u ? SessionTableRenderer.FormatElapsed(u) : "[dim]—[/]";
            var busy = string.IsNullOrWhiteSpace(e.ActivityBefore) ? "[dim]простой[/]" : Markup.Escape(e.ActivityBefore!);
            // Смена boot-time — ещё не дефект: выключение кнопкой выглядит так же (бэклог п.93).
            var kind = e.IsFailure
                ? $"[red]{ShutdownKind.Describe(e.Kind)}[/]"
                : $"[dim]{ShutdownKind.Describe(e.Kind)}[/]";
            // Источник важен: событие из журнала клиента могло случиться до того, как машина
            // вообще попала к нам под наблюдение (бэклог п.97).
            var src = e.Source == RebootSource.Journal ? "[grey]журнал[/]" : "[dim]hub[/]";
            rebootTable.AddRow($"{e.At.ToLocalTime():dd.MM HH:mm:ss}", kind, src, held, busy);
        }
        AnsiConsole.Write(rebootTable);
        if (timeline.WatchingSince is { } watching)
            AnsiConsole.MarkupLineInterpolated(
                $"[grey]Под наблюдением hub с {watching.ToLocalTime():dd.MM HH:mm}; более ранние строки — из журнала клиента.[/]");
        PrintRebootTotals(timeline);
        break;
    }

    case "target" when args.Length >= 2:
        var t = await client.GetTargetAsync(args[1]);
        if (t is null) AnsiConsole.MarkupLineInterpolated($"[red]СЗ {args[1]} не найдена.[/]");
        else AnsiConsole.WriteLine(t.Ssh);
        break;

    case "kb" when args.Length >= 2:
        return await KbCommand.RunAsync(args[1..], options.KbRoot);

    case "hw" when args.Length >= 2:
        await HwCommand.RunAsync(args[1..], ResolveLocal(options.GpuDbPath), ResolveLocal(options.PciIdsPath));
        break;

    // Метка конфигурации обязательна: прогон без неё через неделю нечитаем — непонятно,
    // что с чем сравнивать (на 160697 так потерялись обе половины дискриминатора).
    case "test" when args.Length >= 3 && args[1].Equals("run", StringComparison.OrdinalIgnoreCase):
    {
        var (testFilter, testConfig, sameConfig) = TestRunArgs.Parse(args[3..]);
        var testResult = await client.TriggerTestAsync(args[2], testFilter, testConfig, sameConfig);
        if (testResult.Ok)
        {
            var scope = testFilter is null ? "весь набор" : $"фильтр: {testFilter}";
            var label = testConfig ?? "как в прошлый раз";
            AnsiConsole.MarkupLineInterpolated(
                $"[green]СЗ {args[2]}: прогон запущен[/] ({scope}, конфигурация: {label}) на агенте (отчёт появится в kb).");
        }
        else
        {
            var reason = testResult.Error ?? $"СЗ {args[2]} не найдена среди активных";
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {args[2]}: прогон не запущен.[/] {reason}");
            return 2;
        }
        break;
    }

    // Секции принимаем и через запятую, и несколькими аргументами; опечатка — ошибка, а не
    // молчаливое «собралась одна system» (бэклог п.6). Реальный список печатаем эхом.
    case "diag" when args.Length >= 3 && args[1].Equals("run", StringComparison.OrdinalIgnoreCase):
    {
        var diagSz = args[2];
        var (parsedSections, unknownSections) = DiagSections.Parse(args[3..]);
        if (unknownSections.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Неизвестные секции:[/] {string.Join(", ", unknownSections)}");
            AnsiConsole.MarkupLineInterpolated(
                $"[grey]Доступны:[/] {string.Join(", ", DiagSections.All)}[grey], а также[/] all[grey] и алиасы[/] {string.Join(", ", DiagSections.Aliases.Keys)}");
            return 2;
        }

        var diagSections = parsedSections is null ? null : string.Join(",", parsedSections);
        if (await client.TriggerDiagAsync(diagSz, diagSections))
        {
            var scope = diagSections is null ? "все секции" : $"секции: {diagSections}";
            // Путь печатаем сразу: файл ложится не в корень СЗ, а в reports\<timestamp>\, и
            // ждать его «где-то в kb» приходилось вслепую (бэклог п.60).
            var reportPath = Path.Combine(new KbPaths(options.KbRoot).ReportsDir(diagSz), "<YYYYMMDD-HHMMSS>", "diag.md");
            AnsiConsole.MarkupLineInterpolated($"[green]СЗ {diagSz}: диагностика запущена[/] ({scope}) на агенте.");
            AnsiConsole.MarkupLineInterpolated($"[grey]Отчёт появится здесь:[/] {reportPath}");
        }
        else
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {diagSz} не найдена[/] среди активных.");
        break;
    }

    // push: доставить инструмент на клиента через hub (вместо SMB, который не поднимается).
    case "push" when args.Length >= 2 && (args[1] == "--list" || args[1] == "-l"):
    {
        var catalog = await client.GetToolsAsync();
        if (catalog is null)
        {
            AnsiConsole.MarkupLine("[red]Hub не ответил на запрос каталога инструментов.[/]");
            return 1;
        }

        // Путь печатаем всегда: пустой список без него читается как «инструментов нет»,
        // хотя на деле hub смотрит не в тот каталог (бэклог п.67).
        AnsiConsole.MarkupLineInterpolated($"[grey]Каталог раздачи (Hub.ToolsRoot):[/] {catalog.Root}");
        var tools = catalog.Tools;
        if (tools.Count == 0)
        {
            AnsiConsole.MarkupLine(catalog.Exists
                ? "[yellow]Каталог существует, но инструментов в нём нет[/] — положи их подпапками (occt, tm5, furmark…)."
                : "[red]Каталога нет[/] — задай его при сборке: .\\tools\\build-dist.ps1 -ToolsRoot <путь>");
            return 1;
        }
        foreach (var tool in tools)
            AnsiConsole.MarkupLineInterpolated(
                $"  [yellow]{tool.Name}[/]  {tool.Files} файлов, {tool.Bytes / 1024d / 1024d:N1} МБ");
        break;
    }

    case "push" when args.Length >= 3:
    {
        var pushSz = args[1];
        var tool = args[2];
        var res = await client.PushAsync(pushSz, tool);
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {pushSz} не найдена[/] среди активных.");
            return 1;
        }
        if (!string.IsNullOrEmpty(res.Error))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Доставка не удалась:[/] {res.Error}");
            return 1;
        }
        AnsiConsole.MarkupLineInterpolated(
            $"[green]✓ {tool}[/] → {res.TargetDir} (скачано {res.Downloaded}, уже было {res.Skipped}, {res.Bytes / 1024d / 1024d:N1} МБ)");
        break;
    }

    // pull: забрать файл(ы) с клиента (дампы, CSV, логи). Путь может быть маской или папкой;
    // путей можно перечислить несколько, -r обходит подпапки (бэклог п.75).
    case "pull" when args.Length >= 3:
        return await PullCommand.RunAsync(client, args);

    // exec --result <jobId>: забрать состояние и хвост вывода фоновой задачи. Короткий
    // запрос — проходит даже под полной нагрузкой, когда обычный exec уже не проходит.
    case "exec" when args.Length >= 4 && args[2].Equals("--result", StringComparison.OrdinalIgnoreCase):
    {
        var tailIdx = Array.FindIndex(args, a => a.Equals("--tail", StringComparison.OrdinalIgnoreCase));
        var tailLines = tailIdx >= 0 && args.Length > tailIdx + 1 && int.TryParse(args[tailIdx + 1], out var tl)
            ? tl : ExecLimits.DefaultTailLines;
        var status = await client.ExecStatusAsync(args[1], args[3], tailLines);
        if (status is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {args[1]} не найдена[/] среди активных.");
            return 1;
        }
        if (!string.IsNullOrEmpty(status.Error))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{status.Error}[/]");
            return 1;
        }
        var state = status.Running
            ? $"[yellow]выполняется[/] ({SessionTableRenderer.FormatElapsed(DateTimeOffset.Now - status.StartedAt)})"
            : $"[green]завершена[/] (exit {status.ExitCode})";
        // MarkupLine, а не MarkupLineInterpolated: последний экранирует вставленные значения,
        // и разметка из $state печаталась как текст «[green]завершена[/]» (260306).
        AnsiConsole.MarkupLine($"Задача {Markup.Escape(args[3])}: {state}, вывода {status.OutputBytes} б");
        if (!string.IsNullOrEmpty(status.Tail)) Console.WriteLine(status.Tail);
        return 0;
    }

    // exec: ad-hoc PowerShell на агенте (без SSH). Скрипт строкой или -f <файл>.
    case "exec" when args.Length >= 3:
    {
        var execSz = args[1];
        string script;
        if (args[2].Equals("-f", StringComparison.OrdinalIgnoreCase) && args.Length >= 4)
        {
            if (!File.Exists(args[3]))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Файл не найден:[/] {args[3]}");
                break;
            }
            script = await File.ReadAllTextAsync(args[3]);
            // Скрипт из файла шелл не портит, но сам PowerShell может понять его не так:
            // запятая связывает сильнее «+», и конкатенация внутри массива разваливается
            // на отдельные строки (бэклог п.77).
            foreach (var w in ScriptLint.Check(script))
                AnsiConsole.MarkupLineInterpolated($"[yellow]⚠ {w}[/]");
        }
        else
        {
            script = args[2];
            // Inline-строка проходит через вызывающий шелл, и он успевает её испортить:
            // съесть $-переменные, бэктики, подставить XML-эскейп в `_x` (бэклог п.24/п.28).
            // Предупреждаем ДО запуска — иначе диагноз ищется в CLIXML-простыне.
            var warn = CliXml.WarnAboutInline(script);
            if (warn is not null) AnsiConsole.MarkupLineInterpolated($"[yellow]⚠ {warn}[/]");
            foreach (var w in ScriptLint.Check(script))
                AnsiConsole.MarkupLineInterpolated($"[yellow]⚠ {w}[/]");
        }

        int? execTimeout = null;
        var tIdx = Array.FindIndex(args, a => a.Equals("--timeout", StringComparison.OrdinalIgnoreCase));
        if (tIdx >= 0 && args.Length > tIdx + 1 && int.TryParse(args[tIdx + 1], out var parsedTimeout))
            execTimeout = parsedTimeout;

        var detach = args.Any(a => a.Equals("--detach", StringComparison.OrdinalIgnoreCase));

        var execRes = await client.ExecAsync(execSz, script, execTimeout, default, detach);
        if (execRes is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {execSz} не найдена[/] среди активных.");
            return 1;
        }
        // CLIXML разворачиваем на своей стороне: ошибка PowerShell должна читаться как
        // ошибка, а не как XML-дамп с _x000D__x000A_ вместо переносов (бэклог п.28).
        if (!string.IsNullOrEmpty(execRes.StdOut)) Console.WriteLine(CliXml.Decode(execRes.StdOut).TrimEnd());
        if (!string.IsNullOrEmpty(execRes.StdErr))
            AnsiConsole.MarkupLineInterpolated($"[yellow]stderr:[/] {CliXml.Decode(execRes.StdErr).TrimEnd()}");
        if (execRes.JobId is not null)
            AnsiConsole.MarkupLineInterpolated($"[grey]Фоновая задача:[/] szcli exec {execSz} --result {execRes.JobId}");
        if (execRes.Truncated) AnsiConsole.MarkupLine("[yellow]⚠ вывод обрезан по лимиту[/]");
        if (execRes.TimedOut) AnsiConsole.MarkupLine("[red]⚠ скрипт остановлен по таймауту[/]");
        if (execRes.ExitCode != 0)
            AnsiConsole.MarkupLineInterpolated($"[yellow]exit code: {execRes.ExitCode}[/]");
        break;
    }

    default:
        PrintUsage();
        break;
}
}
catch (Exception ex) when (CliErrors.IsExpected(ex))
{
    AnsiConsole.MarkupLineInterpolated($"[red]{CliErrors.Describe(ex, options.HubBaseUrl)}[/]");
    return CliErrors.ExitCode(ex);
}

return 0;

static void PrintUsage()
{
        AnsiConsole.Write(new Rule("[bold]sz-diag[/]").LeftJustified());
        AnsiConsole.MarkupLine("""
            Использование:
              [yellow]szcli[/] [grey][[watch]][/]          живой список онлайн-СЗ (по умолчанию)
              [yellow]szcli list[/]             однократный список
              [yellow]szcli close[/] [blue]<СЗ>[/]         закрыть СЗ (revert на агенте)
              [yellow]szcli target[/] [blue]<СЗ>[/]        SSH-адрес по номеру СЗ
              [yellow]szcli reboots[/] [blue]<СЗ>[/]       таймлайн вырубонов (по смене boot-time)
              [yellow]szcli note[/] [blue]<СЗ>[/] [grey]<текст>[/]  ручной шаг в журнал СЗ (свап железа, BIOS, осмотр)
                [grey]принимается и когда машина offline или СЗ закрыта[/]
              [yellow]szcli sensors[/] [grey]start|status|stop <СЗ> | report <csv>[/]
                [grey]наблюдатель нагрузки (CSV построчно, переживает вырубон) и его разбор[/]
              [yellow]szcli freeze[/] [blue]<СЗ>[/] [grey][[--status]][/]  заморозить Windows Update (или проверить, держится ли)
              [yellow]szcli unfreeze[/] [blue]<СЗ>[/]      вернуть Windows Update как было (обязательно!)
              [yellow]szcli test run[/] [blue]<СЗ>[/] [grey][[occt|tm5,furmark|…]][/] [red]--config[/] [grey]"<конфигурация>"[/]
                [grey]прогон тестов; метка конфигурации обязательна («EXPO 6000, штатный БП»),[/]
                [grey]повторить ту же — --same-config[/]
              [yellow]szcli diag run[/] [blue]<СЗ>[/] [grey][[storage,events|…]][/]  диагностика (снапшот; секции точечно)
                [grey]секции: system cpu memory gpu storage temps drivers events reboots whea livekernel reliability battery[/]
                [grey]можно через запятую или пробел; all — все; алиасы: hw ram disks video bsod tdr temp[/]
              [yellow]szcli exec[/] [blue]<СЗ>[/] [grey]"<powershell>" | -f <файл> [[--timeout <сек>]] [[--detach]][/]
              [yellow]szcli exec[/] [blue]<СЗ>[/] [grey]--result <jobId> [[--tail N]]   состояние фоновой задачи[/]
                [grey]выполнить скрипт на агенте и получить вывод (без SSH)[/]
                [grey]всё сложнее однострочника — через [/][yellow]-f[/][grey]: inline-строку портит твой шелл[/]
              [yellow]szcli push[/] [blue]<СЗ>[/] [grey]<tool> | --list[/]
                [grey]доставить инструмент на клиента через hub (клиент качает сам, без SMB)[/]
              [yellow]szcli pull[/] [blue]<СЗ>[/] [grey]<путь…> [[--max-mb N]] [[-r]][/]
                [grey]забрать файлы (маска [/]*.dmp[grey], папка или несколько путей) в[/] hub\pulled\<СЗ>\<время>\
                [grey]-r — с подпапками (LiveKernelReports держит дампы в[/] WATCHDOG*[grey])[/]
              [yellow]szcli agent restart[/] [blue]<СЗ>[/]  поднять агента заново (задачей под SYSTEM, без похода к машине)
              [yellow]szcli agent set[/] [blue]<СЗ>[/] [grey]WatchdogHours=12[/]  правка конфига агента с хоста
              [yellow]szcli client[/] [grey]info|cleanup <СЗ>[/]  следы прогонов на клиенте и их уборка
              [yellow]szcli maintenance[/] [blue]<СЗ>[/] [grey]"причина" [[--from 18:30]] [[--until 19:15]] | --list[/]
                [grey]метка «работали руками»: события питания в окне — не вырубон[/]
              [yellow]szcli kb[/] …               работа с базой знаний ([grey]record/summary/search/rm[/])
              [yellow]szcli hw[/] …               видяха по PCI hardware id
            [grey]Номер СЗ — 6 цифр.[/]
            """);
}

/// <summary>Сводка по вырубонам: сколько и максимальный аптайм. Печатается при закрытии СЗ
/// и после таймлайна — «за время у нас машина падала N раз» должно попадаться на глаза само.</summary>
static void PrintRebootTotals(RebootTimeline timeline)
{
    var max = timeline.MaxUptime is { } m ? SessionTableRenderer.FormatElapsed(m) : "неизвестно";
    // Считаем отдельно: «5 вырубонов, машина сыпется» на 161312 на деле означало три обрыва
    // и два выключения кнопкой — и вердикт по заявке менялся вместе с этим (бэклог п.93).
    var failures = timeline.Events.Count(e => e.IsFailure);
    var benign = timeline.Count - failures;
    var tail = benign > 0 ? $" (плюс {benign} штатных: кнопка/перезагрузка)" : "";
    AnsiConsole.MarkupLineInterpolated(
        $"[yellow]Вырубонов: {failures}[/]{tail}. Максимальный аптайм между ними: {max}.");
}

static async Task PrintRebootSummaryAsync(IHubApiClient client, string sz)
{
    try
    {
        var timeline = await client.GetRebootsAsync(sz);
        if (timeline is null || timeline.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]За время у нас вырубонов не зафиксировано.[/]");
            return;
        }
        AnsiConsole.MarkupLineInterpolated($"[grey]За время у нас:[/] подробности — szcli reboots {sz}");
        PrintRebootTotals(timeline);
    }
    catch (Exception ex)
    {
        // Сводка не должна ломать закрытие СЗ — оно уже произошло.
        AnsiConsole.MarkupLineInterpolated($"[grey]Сводку по вырубонам получить не удалось: {ex.Message}[/]");
    }
}

static string ResolveLocal(string path)
    => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

static async Task WatchAsync(IHubApiClient client)
{
    AnsiConsole.Write(new Rule("[bold]sz-diag[/] — онлайн-СЗ").LeftJustified());
    AnsiConsole.MarkupLine("[grey]Ctrl+C для выхода.[/]\n");

    var table = SessionTableRenderer.Render(Array.Empty<SzDiag.Contracts.SessionInfo>());
    await AnsiConsole.Live(table)
        .AutoClear(false)
        .Overflow(VerticalOverflow.Ellipsis)
        .Cropping(VerticalOverflowCropping.Bottom)
        .StartAsync(async ctx =>
        {
            while (true)
            {
                IReadOnlyList<SzDiag.Contracts.SessionInfo> sessions;
                try
                {
                    sessions = await client.GetSessionsAsync();
                }
                catch (HttpRequestException)
                {
                    var offline = new Table().Border(TableBorder.Rounded).BorderColor(Color.Red);
                    offline.AddColumn(new TableColumn("[red]hub недоступен, переподключение…[/]"));
                    ctx.UpdateTarget(offline);
                    ctx.Refresh();
                    await Task.Delay(2000);
                    continue;
                }

                ctx.UpdateTarget(SessionTableRenderer.Render(sessions).Caption($"обновлено {DateTime.Now:HH:mm:ss}"));
                ctx.Refresh();
                await Task.Delay(1000);
            }
        });
}
