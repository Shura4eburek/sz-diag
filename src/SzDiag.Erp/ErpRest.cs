using System.Text.Json;

namespace SzDiag.Erp;

/// <summary>Строка состава ПК из `sz.get`: серийников у одной позиции может быть несколько.</summary>
public sealed record RestComponent(string Name, IReadOnlyList<string> Serials, string Quantity);

public sealed record RestSibling(string Id, string State, string Product);

/// <summary>
/// Сводка ответа `sz.get` для печати. Полный ответ — сырым json в kb: типизировано только
/// то, что показывается человеку и ИИ в терминале.
/// </summary>
/// <param name="Source">prefab | our_build | no_our_assembly — откуда состав (см. docs/teleauto-rest-api.md).</param>
/// <param name="Components">null — состава от нас нет (no_our_assembly), а не «пустой».</param>
public sealed record SzGetResult(
    string Number,
    string State,
    string OrderId,
    string Defect,
    string Comment,
    string ServicedItem,
    string Source,
    string? Series,
    string? AssemblyId,
    IReadOnlyList<RestComponent>? Components,
    int Discussions,
    int Repairs,
    IReadOnlyList<RestSibling> Siblings,
    string? Phone,
    int? CustomerOrdersTotal);

public sealed record CustomerOrderLine(string Name, string Quantity, bool IsService);

public sealed record CustomerOrder(
    string Id, string CreatedOn, string State, IReadOnlyList<CustomerOrderLine> Products);

public sealed record CustomerOrders(string Phone, int Total, IReadOnlyList<CustomerOrder> Orders);

/// <summary>
/// REST-путь TeleAuto (с v1.6.0): `sz.get` и `api.*` читают бэкенд Telemart напрямую —
/// без окон, захвата и физических кликов, поэтому их можно звать и из сессии заявки.
/// Имена полей — как отдаёт бэкенд (snake_case, с его опечаткой `apppearance`).
/// </summary>
public static class ErpRest
{
    public const string SzGetTool = "sz.get";
    public const string CustomerOrdersTool = "api.customer.orders";

    /// <summary>
    /// Сквозной вызов открыт только REST-инструментам. UI-инструменты (`session.*`,
    /// `filters.*`, `result.open`…) кликают по экрану — через `sz call` их звать нельзя.
    /// </summary>
    public static bool IsCallable(string tool)
        => tool == SzGetTool || tool.StartsWith("api.", StringComparison.Ordinal);

    /// <summary>
    /// Аргументы `sz call`: `ключ=значение` через пробел (`order_id=1951256 limit=5`) либо один
    /// json-объект. Основная форма — ключ=значение: json из PowerShell через `szcli.cmd` теряет
    /// внутренние кавычки (сессия Desk на этом споткнулась, бэклог п.269), а у `ключ=значение`
    /// экранировать нечего. Числа и true/false уходят как json-числа и булевы.
    /// </summary>
    /// <returns>Аргументы (null — без аргументов) либо текст ошибки для человека.</returns>
    public static (object? Args, string? Error) ParseCallArgs(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return (null, null);

        if (args.Count == 1 && args[0].TrimStart().StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(args[0]);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return (document.RootElement.Clone(), null);
            }
            catch (JsonException) { }
            return (null, "json не разобран (из PowerShell кавычки срезаются) — передай аргументы "
                + "как ключ=значение, например: sz call api.order order_id=1951256");
        }

        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var arg in args)
        {
            var at = arg.IndexOf('=');
            if (at <= 0)
                return (null, $"«{arg}» — не ключ=значение, например: sz call api.order order_id=1951256");
            var value = arg[(at + 1)..];
            result[arg[..at]] = long.TryParse(value, out var number) ? number
                : bool.TryParse(value, out var flag) ? flag
                : value;
        }
        return (result, null);
    }

    /// <summary>Словарь статусов заявки Telemart (`ServiceRequestState`).</summary>
    public static string RequestState(string id) => id switch
    {
        "1" => "Звернення",
        "2" => "В роботі",
        "3" => "Готова",
        "4" => "У СЦ",
        "5" => "Прийнята",
        "6" => "Завершена",
        "7" => "Скасована",
        "8" => "На узгодженні",
        "" => "",
        _ => $"статус {id}",
    };

    public static SzGetResult ParseSzGet(JsonElement root)
    {
        var request = ErpJson.Node(root, "request");
        var configuration = ErpJson.Node(root, "configuration");
        var customer = ErpJson.Node(root, "customer");
        var serviced = ErpJson.Node(configuration, "serviced_item");

        return new SzGetResult(
            Number: Text(request, "id"),
            State: RequestState(Text(request, "state_id")),
            OrderId: Text(request, "order_id"),
            Defect: Text(request, "stated_defect"),
            Comment: Text(request, "comment"),
            ServicedItem: Text(serviced, "name") is { Length: > 0 } item ? item : Text(request, "product_name"),
            Source: Text(configuration, "source"),
            Series: NullIfBlank(Text(configuration, "series")),
            AssemblyId: NullIfBlank(Text(ErpJson.Node(configuration, "assembly"), "id")),
            Components: ErpJson.Node(configuration, "components") is { ValueKind: JsonValueKind.Array } list
                ? list.EnumerateArray().Select(c => new RestComponent(
                    Text(c, "name"), Strings(ErpJson.Node(c, "serial_numbers")), Text(c, "quantity"))).ToList()
                : null,
            Discussions: Count(ErpJson.Node(root, "discussions")),
            Repairs: Count(ErpJson.Node(root, "repairs")),
            Siblings: Items(ErpJson.Node(root, "siblings"))
                .Select(s => new RestSibling(Text(s, "id"), RequestState(Text(s, "state_id")), Text(s, "product_name")))
                .ToList(),
            Phone: NullIfBlank(Text(customer, "phone")),
            CustomerOrdersTotal: int.TryParse(Text(customer, "orders_total"), out var total) ? total : null);
    }

    public static CustomerOrders ParseCustomerOrders(JsonElement root)
    {
        var orders = Items(ErpJson.Node(root, "orders")).Select(o => new CustomerOrder(
            Text(o, "id"),
            Text(o, "created_on"),
            Text(o, "state_text"),
            Items(ErpJson.Node(o, "products")).Select(p => new CustomerOrderLine(
                Text(p, "name"),
                Text(p, "quantity"),
                ErpJson.Node(p, "is_service") is { ValueKind: JsonValueKind.True })).ToList())).ToList();

        return new CustomerOrders(
            Text(root, "phone"),
            int.TryParse(Text(root, "total"), out var total) ? total : orders.Count,
            orders);
    }

    /// <summary>
    /// Аргумент `sz orders`: номер СЗ (ровно 6 цифр) — телефон берётся из заявки,
    /// иначе это телефон в любом виде (+380…, 380…, 0…, с пробелами и скобками).
    /// </summary>
    public static object CustomerOrdersArgs(string subject, int? limit)
    {
        var digits = new string(subject.Where(char.IsDigit).ToArray());
        var bySz = subject.Trim().Length == 6 && digits.Length == 6;
        return (bySz, limit) switch
        {
            (true, null) => new { number = digits },
            (true, { } l) => new { number = digits, limit = l },
            (false, null) => new { phone = subject },
            (false, { } l) => new { phone = subject, limit = l },
        };
    }

    private static string Text(JsonElement? parent, string name)
        => ErpJson.Node(parent, name) is { } node ? ErpJson.Scalar(node) : "";

    private static IEnumerable<JsonElement> Items(JsonElement? node)
        => node is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    private static int Count(JsonElement? node)
        => node is { ValueKind: JsonValueKind.Array } array ? array.GetArrayLength() : 0;

    private static IReadOnlyList<string> Strings(JsonElement? node)
        => Items(node).Select(ErpJson.Scalar).Where(s => s.Length > 0).ToList();

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Сырой ответ `sz.get` — в `kb/СЗ/&lt;номер&gt;/erp-rest.json`. Отдельный файл, а не `erp.json`:
/// у `sz fetch` там другая форма, и его разбор на неё рассчитан. `запит.md` не трогаем —
/// блок `erp:` принадлежит `sz fetch`.
/// </summary>
public static class SzGetWriter
{
    public const string FileName = "erp-rest.json";

    public static string Save(string kbRoot, string sz, string rawJson)
    {
        var dir = new SzDiag.Kb.KnowledgeBaseScaffolder(kbRoot).EnsureSkeleton(sz);
        var path = Path.Combine(dir, FileName);
        File.WriteAllText(path, rawJson);
        return path;
    }
}
