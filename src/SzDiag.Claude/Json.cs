using System.Globalization;
using System.Text.Json;

namespace SzDiag.Claude;

/// <summary>Терпимое чтение полей: stream-json — чужой формат, отсутствующее поле не повод падать.</summary>
internal static class Json
{
    public static string? Str(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static bool Bool(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static long Long(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : 0;

    public static DateTimeOffset? Time(JsonElement o)
        => Str(o, "timestamp") is { } s
           && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;
}
