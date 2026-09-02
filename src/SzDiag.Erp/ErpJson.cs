using System.Text.Json;

namespace SzDiag.Erp;

/// <summary>Разбор ответа API в DTO. Отсутствующие узлы — не ошибка, а пустота.</summary>
public static class ErpJson
{
    public static SzFetchResult ParseSzFetch(JsonElement root) => new(
        ParseRequest(Node(root, "request")),
        ParseOrder(Node(root, "order")),
        ParseAssembly(Node(root, "assembly")));

    private static ErpRequest ParseRequest(JsonElement? node) => new(
        Str(node, "number"),
        Map(Node(node, "fields")),
        Components(Node(node, "components")),
        NullIfBlank(Str(node, "order_number")));

    private static ErpOrder? ParseOrder(JsonElement? node) => node is null ? null : new ErpOrder(
        Str(node, "number"),
        Map(Node(node, "fields")),
        Rows(Node(node, "products")));

    private static ErpAssembly? ParseAssembly(JsonElement? node) => node is null ? null : new ErpAssembly(
        Str(node, "number"),
        Str(node, "summary"),
        Components(Node(node, "components")));

    private static JsonElement? Node(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } obj) return null;
        if (!obj.TryGetProperty(name, out var child)) return null;
        return child.ValueKind == JsonValueKind.Null ? null : child;
    }

    private static string Str(JsonElement? parent, string name)
        => Node(parent, name) is { } n && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IReadOnlyDictionary<string, string> Map(JsonElement? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is not { ValueKind: JsonValueKind.Object } obj) return map;
        foreach (var property in obj.EnumerateObject())
            map[property.Name] = Scalar(property.Value);
        return map;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> Rows(JsonElement? node)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        if (node is not { ValueKind: JsonValueKind.Array } array) return rows;
        foreach (var row in array.EnumerateArray())
            rows.Add(Map(row));
        return rows;
    }

    private static IReadOnlyList<ErpComponent> Components(JsonElement? node)
    {
        var items = new List<ErpComponent>();
        if (node is not { ValueKind: JsonValueKind.Array } array) return items;
        foreach (var item in array.EnumerateArray())
            items.Add(new ErpComponent(
                Str(item, "code"), Str(item, "name"), Str(item, "serial"), Str(item, "quantity")));
        return items;
    }

    /// <summary>Значения приводим к строке: на той стороне числа и строки перемешаны.</summary>
    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => value.ToString(),
    };
}
