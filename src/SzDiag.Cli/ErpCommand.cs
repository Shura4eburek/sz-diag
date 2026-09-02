using System.Text.Encodings.Web;
using System.Text.Json;
using Spectre.Console;
using SzDiag.Erp;

namespace SzDiag.Cli;

/// <summary>
/// `szcli sz fetch &lt;СЗ&gt;` — подтянуть данные заявки из учётной системы в базу знаний.
/// `szcli sz release` — отпустить залипший захват.
/// </summary>
public static class ErpCommand
{
    /// <summary>
    /// Код API → код возврата. Разные причины сбоя различаются кодом, потому что
    /// «не запущено», «занято» и «интерфейс не распознан» лечатся по-разному.
    /// </summary>
    public static int ExitCodeFor(string apiCode) => apiCode switch
    {
        "unavailable" or "no_token" => 3,
        "client_not_running" or "client_not_logged_in" => 4,
        "busy" => 5,
        "not_found" or "ambiguous" => 6,
        // Ловили на живом съёме: захват берётся, логин на месте, а навигация по разделам
        // не находится, потому что окно с описанием обновления обнуляет дерево
        // автоматизации. Сбой не наш и лечится не так, как «не запущено», — свой код.
        "anchor_missing" => 7,
        _ => 1,
    };

    public static async Task<int> RunAsync(string[] args, CliOptions options)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (sub is "--help" or "-h" or "help" or "")
        {
            PrintUsage();
            return sub == "" ? 2 : 0;
        }

        return sub switch
        {
            "fetch" when args.Length >= 2 => await FetchAsync(args[1], args.Contains("--force"), options),
            "release" => await ReleaseAsync(options),
            _ => Unknown(),
        };

        static int Unknown()
        {
            PrintUsage();
            return 2;
        }
    }

    private static async Task<int> FetchAsync(string sz, bool force, CliOptions options)
    {
        ErpApiClient client;
        try { client = ErpApiClient.Create(options.Erp); }
        catch (ErpApiException e) { return Fail(e); }

        using (client)
        {
            if (!await client.IsAliveAsync())
            {
                AnsiConsole.MarkupLine("[red]API учётной системы не отвечает.[/] "
                    + "Проверь, что сервис поднят, а адрес в секции [grey]Erp[/] конфига верный.");
                return 3;
            }

            // Автоматизация кликает физически: увёл мышь — увёл клик.
            AnsiConsole.MarkupLine("[yellow]Идёт обращение к учётной системе (несколько минут). "
                + "Не трогай мышь и клавиатуру.[/]");

            JsonElement result;
            try
            {
                await using var session = await ErpSession.BeginAsync(client);
                result = await client.CallAsync("sz.fetch", new { number = sz });
            }
            catch (ErpApiException e) { return Fail(e); }

            var raw = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            var data = ErpJson.ParseSzFetch(result);
            var written = new SzFetchWriter(options.KbRoot).Write(sz, data, raw, force);
            Print(sz, data, written);
            return 0;
        }
    }

    /// <summary>Захват снаружи иначе не снять: прибитый процесс оставляет его висеть.</summary>
    private static async Task<int> ReleaseAsync(CliOptions options)
    {
        ErpApiClient client;
        try { client = ErpApiClient.Create(options.Erp); }
        catch (ErpApiException e) { return Fail(e); }

        using (client)
        {
            try { await client.CallAsync(ErpSession.EndTool); }
            catch (ErpApiException e) { return Fail(e); }
            AnsiConsole.MarkupLine("Захват отпущен.");
            return 0;
        }
    }

    private static void Print(string sz, SzFetchResult data, SzFetchWriteResult written)
    {
        AnsiConsole.MarkupLineInterpolated($"СЗ {sz}: записано в базу знаний.");
        AnsiConsole.MarkupLineInterpolated($"  сырой ответ: {written.JsonPath}");

        if (data.Request.Fields.TryGetValue("Дефект", out var defect) && !string.IsNullOrWhiteSpace(defect))
            AnsiConsole.MarkupLineInterpolated($"  дефект: {Inline(defect)}");
        if (data.Request.OrderNumber is { } order)
            AnsiConsole.MarkupLineInterpolated($"  заказ: {order}");
        if (written.DeviceSkipReason is { } reason)
            AnsiConsole.MarkupLineInterpolated($"  пристрій не визначено: {reason}");

        if (data.Configuration.Count == 0)
        {
            AnsiConsole.MarkupLine("  [yellow]состав неизвестен: ни збірки, ни комплектации, ни позиций заказа[/]");
            return;
        }

        var withSerials = data.Source is ConfigurationSource.Assembly or ConfigurationSource.Request;
        var table = new Table().AddColumns("Компонент", withSerials ? "Серийник" : "—", "К-во");
        foreach (var component in data.Configuration)
            table.AddRow(
                Markup.Escape(component.Name),
                Markup.Escape(component.Serial),
                Markup.Escape(component.Quantity));
        AnsiConsole.Write(table);

        if (!withSerials)
            AnsiConsole.MarkupLine("  [grey]состав из строк заказа — серийников там нет[/]");
    }

    private static string Inline(string value)
        => value.Replace("\r", "").Replace("\n", " ").Trim();

    private static int Fail(ErpApiException e)
    {
        var code = ExitCodeFor(e.Code);
        var hint = code switch
        {
            3 => "Сервис не поднят либо нет файла токена.",
            4 => "Запусти учётную программу и войди в неё, потом повтори.",
            5 => "Захват занят — отпусти его: szcli sz release",
            6 => "Заявка не найдена либо совпадений больше одного.",
            7 => "Интерфейс учётной программы не распознан. Чаще всего это окно с "
                 + "описанием обновления поверх рабочей области — закрой его и повтори.",
            _ => "",
        };
        AnsiConsole.MarkupLineInterpolated($"[red]Сбой обращения к учётной системе[/] ({e.Code}): {e.Message}");
        if (hint.Length > 0) AnsiConsole.MarkupLineInterpolated($"  {hint}");
        return code;
    }

    private static void PrintUsage() => Console.WriteLine("""
        Использование:
          szcli sz fetch <СЗ> [--force]   подтянуть данные заявки из учётной системы в kb
          szcli sz release                отпустить залипший захват учётной программы

        --force перебивает уже заполненные поля frontmatter (по умолчанию не трогаются).
        Вызов занимает несколько минут и кликает по чужому интерфейсу физически:
        пока он идёт, мышь и клавиатуру не трогать.
        """);
}
