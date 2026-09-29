using System.Text.Encodings.Web;
using System.Text.Json;
using Spectre.Console;
using SzDiag.Erp;

namespace SzDiag.Cli;

/// <summary>
/// `szcli sz fetch &lt;СЗ&gt;` — подтянуть данные заявки из учётной системы в базу знаний.
/// `szcli sz release` — отпустить залипший захват.
/// `szcli sz get|orders|call` — REST-путь TeleAuto (docs/teleauto-rest-api.md): без окон и
/// захвата, за секунды, поэтому годится и для сессии заявки.
/// </summary>
public static class ErpCommand
{
    /// <summary>REST-вызов — несколько запросов к бэкенду; минуты ждать незачем.</summary>
    private const int RestTimeoutSeconds = 120;

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
        // REST-путь: три причины среды, и лечатся все по-разному (PIN, VPN, пароль/секрет).
        "locked" => 8,
        "unreachable" => 9,
        "auth_failed" => 10,
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
            "get" when args.Length >= 2 => await GetAsync(args[1], args.Contains("--json"), options),
            "orders" when args.Length >= 2 => await OrdersAsync(args[1], LimitArg(args), args.Contains("--json"), options),
            "call" when args.Length >= 2 => await CallAsync(args[1], args.Length >= 3 ? args[2] : null, options),
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

    /// <summary>`sz get`: всё по заявке одним вызовом; сырой ответ — в kb рядом с заметками.</summary>
    private static async Task<int> GetAsync(string sz, bool json, CliOptions options)
    {
        JsonElement result;
        try { result = await CallRestAsync(ErpRest.SzGetTool, new { number = sz }, options); }
        catch (ErpApiException e) { return Fail(e); }

        var raw = JsonSerializer.Serialize(result, PrettyJson);
        var path = SzGetWriter.Save(options.KbRoot, sz, raw);
        if (json)
        {
            Console.WriteLine(raw);
            return 0;
        }

        PrintSzGet(ErpRest.ParseSzGet(result), path);
        return 0;
    }

    /// <summary>`sz orders`: заказы клиента по телефону — когда ПК собран им самим из нашей комплектухи.</summary>
    private static async Task<int> OrdersAsync(string subject, int? limit, bool json, CliOptions options)
    {
        JsonElement result;
        try
        {
            result = await CallRestAsync(
                ErpRest.CustomerOrdersTool, ErpRest.CustomerOrdersArgs(subject, limit), options);
        }
        catch (ErpApiException e) { return Fail(e); }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, PrettyJson));
            return 0;
        }

        PrintOrders(ErpRest.ParseCustomerOrders(result));
        return 0;
    }

    /// <summary>`sz call`: сквозной вызов REST-инструмента, ответ — json как есть.</summary>
    private static async Task<int> CallAsync(string tool, string? argsJson, CliOptions options)
    {
        if (!ErpRest.IsCallable(tool))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]{tool}[/] через sz call не вызывается: открыты только sz.get и api.*. UI-инструменты кликают по экрану — для них есть sz fetch.");
            return 2;
        }

        JsonElement? args = null;
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try
            {
                using var document = JsonDocument.Parse(argsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException();
                args = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                AnsiConsole.MarkupLine("[red]Аргументы — json-объект[/], например "
                    + "[grey]'{\"order_id\": 1951256}'[/].");
                return 2;
            }
        }

        JsonElement result;
        try { result = await CallRestAsync(tool, args, options); }
        catch (ErpApiException e) { return Fail(e); }

        Console.WriteLine(JsonSerializer.Serialize(result, PrettyJson));
        return 0;
    }

    /// <summary>REST не трогает окна: без ErpSession (захват лишь заблокировал бы sz fetch).</summary>
    private static async Task<JsonElement> CallRestAsync(string tool, object? args, CliOptions options)
    {
        using var client = ErpApiClient.Create(options.Erp, RestTimeoutSeconds);
        if (!await client.IsAliveAsync())
            throw new ErpApiException("unavailable", "TeleAuto не отвечает.");
        return await client.CallAsync(tool, args);
    }

    private static int? LimitArg(string[] args)
    {
        var at = Array.IndexOf(args, "--limit");
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var limit) && limit > 0
            ? limit
            : null;
    }

    private static void PrintSzGet(SzGetResult data, string path)
    {
        AnsiConsole.MarkupLineInterpolated($"СЗ {data.Number}: {data.State}, заказ {data.OrderId}");
        if (data.Defect.Length > 0) AnsiConsole.MarkupLineInterpolated($"  дефект: {Inline(data.Defect)}");
        if (data.Comment.Length > 0) AnsiConsole.MarkupLineInterpolated($"  коментар: {Inline(data.Comment)}");
        AnsiConsole.MarkupLineInterpolated($"  изделие заявки: {data.ServicedItem}");
        AnsiConsole.MarkupLineInterpolated($"  переписка: {data.Discussions} · ремонты в СЦ: {data.Repairs}");

        var origin = data.Source switch
        {
            "prefab" => $"собранный у нас ПК, серия {data.Series}, збірка {data.AssemblyId}",
            "our_build" => data.AssemblyId is { } id ? $"наша сборка под заказ, збірка {id}" : "наша сборка под заказ",
            "no_our_assembly" => "нашей сборки нет — у нас куплено только изделие заявки",
            _ => data.Source,
        };
        AnsiConsole.MarkupLineInterpolated($"  состав: {origin}");

        if (data.Components is { Count: > 0 } components)
        {
            var table = new Table().AddColumns("Компонент", "Серийники", "К-во");
            foreach (var c in components)
                table.AddRow(Markup.Escape(c.Name), Markup.Escape(string.Join(", ", c.Serials)),
                    Markup.Escape(c.Quantity));
            AnsiConsole.Write(table);
        }

        if (data.Siblings.Count > 0)
            AnsiConsole.MarkupLineInterpolated(
                $"  другие СЗ на этот заказ: {string.Join(", ", data.Siblings.Select(s => $"{s.Id} ({s.State})"))}");

        if (data.CustomerOrdersTotal is > 1)
            AnsiConsole.MarkupLineInterpolated(
                $"  у клиента {data.CustomerOrdersTotal} заказов — история: szcli sz orders {data.Number}");

        AnsiConsole.MarkupLineInterpolated($"  сырой ответ: {path}");
    }

    private static void PrintOrders(CustomerOrders data)
    {
        AnsiConsole.MarkupLineInterpolated($"Клиент {data.Phone}: заказов {data.Total}, показано {data.Orders.Count}");
        var table = new Table().AddColumns("Заказ", "Дата", "Статус", "Товары");
        foreach (var order in data.Orders)
        {
            var lines = order.Products.Select(p =>
                (p.IsService ? "[услуга] " : "") + p.Name + (p.Quantity is "" or "1" ? "" : $" ×{p.Quantity}"));
            table.AddRow(
                Markup.Escape(order.Id),
                Markup.Escape(order.CreatedOn.Length >= 10 ? order.CreatedOn[..10] : order.CreatedOn),
                Markup.Escape(order.State),
                Markup.Escape(string.Join("\n", lines)));
        }
        AnsiConsole.Write(table);
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
            8 => "TeleAuto заблокирован — введи в нём PIN (и проверь, что сохранены логин и пароль Telemart).",
            9 => "Нет связи с бэкендом Telemart — подними VPN (Pritunl) и повтори.",
            10 => "Telemart не выдал токен: проверь пароль в TeleAuto и файл telemart_client_secret рядом с TeleAuto.exe.",
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
          szcli sz get <СЗ> [--json]      всё по заявке через REST: дефект, состав ПК, повторные СЗ
          szcli sz orders <СЗ|телефон> [--limit N] [--json]
                                          заказы клиента по телефону, с товарами
          szcli sz call <инструмент> ['<json>']
                                          сквозной вызов sz.get / api.*, ответ — json

        sz fetch: --force перебивает уже заполненные поля frontmatter (по умолчанию не трогаются).
        Вызов занимает несколько минут и кликает по чужому интерфейсу физически:
        пока он идёт, мышь и клавиатуру не трогать.

        sz get / orders / call — REST-путь TeleAuto v1.6.0+: без окон и захвата, за секунды.
        Нужны VPN и разблокированный PIN'ом TeleAuto. sz get кладёт сырой ответ в
        kb\СЗ\<номер>\erp-rest.json. Подробно — docs/teleauto-rest-api.md.
        """);
}
