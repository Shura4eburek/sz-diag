using System.Text;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using SzDiag.Cli;
using SzDiag.Kb;

// UTF-8 в консоли — иначе кириллица и рамки таблицы ломаются на Windows.
try { Console.OutputEncoding = Encoding.UTF8; } catch { /* вывод может быть перенаправлен — не критично */ }

// Часть Windows-терминалов не до конца корректно обрабатывает ANSI-цветовые
// escape-последовательности (Spectre.Console автодетект иногда даёт ложный "поддерживает") —
// из-за этого крашенные ячейки (Статус) съезжают по ширине, хотя некрашеные (СЗ, IP, Хост)
// рендерятся ровно. Отключаем ANSI/цвет совсем — тот же режим, что и в тестах рендерера,
// где вёрстка всегда собирается верно. Теряем цвет, выигрываем гарантированное выравнивание.
AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
{
    Ansi = AnsiSupport.No,
    ColorSystem = ColorSystemSupport.NoColors,
    Out = new AnsiConsoleOutput(Console.Out),
});

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
            AnsiConsole.MarkupLineInterpolated($"[green]СЗ {args[1]} закрыта[/] (revert отправлен агенту).");
        else
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {args[1]} не найдена[/] среди активных.");
        break;

    case "target" when args.Length >= 2:
        var t = await client.GetTargetAsync(args[1]);
        if (t is null) AnsiConsole.MarkupLineInterpolated($"[red]СЗ {args[1]} не найдена.[/]");
        else AnsiConsole.WriteLine(t.Ssh);
        break;

    case "kb" when args.Length >= 2:
        await KbCommand.RunAsync(args[1..], options.KbRoot);
        break;

    case "test" when args.Length >= 3 && args[1].Equals("run", StringComparison.OrdinalIgnoreCase):
        var testFilter = args.Length >= 4 ? args[3] : null;
        if (await client.TriggerTestAsync(args[2], testFilter))
        {
            var scope = testFilter is null ? "весь набор" : $"фильтр: {testFilter}";
            AnsiConsole.MarkupLineInterpolated($"[green]СЗ {args[2]}: прогон запущен[/] ({scope}) на агенте (отчёт появится в kb).");
        }
        else
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {args[2]} не найдена[/] среди активных.");
        break;

    default:
        AnsiConsole.Write(new Rule("[bold]sz-diag[/]").LeftJustified());
        AnsiConsole.MarkupLine("""
            Использование:
              [yellow]szcli[/] [grey][[watch]][/]          живой список онлайн-СЗ (по умолчанию)
              [yellow]szcli list[/]             однократный список
              [yellow]szcli close[/] [blue]<СЗ>[/]         закрыть СЗ (revert на агенте)
              [yellow]szcli target[/] [blue]<СЗ>[/]        SSH-адрес по номеру СЗ
              [yellow]szcli test run[/] [blue]<СЗ>[/] [grey][[occt|tm5,furmark|…]][/]  прогон тестов (все или по id)
              [yellow]szcli kb[/] …               работа с базой знаний
            """);
        break;
}

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
