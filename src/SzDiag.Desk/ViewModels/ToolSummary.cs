using System.Text.Json;

namespace SzDiag.Desk.ViewModels;

/// <summary>Одна строка «что делает инструмент» для свёрнутой карточки.</summary>
public static class ToolSummary
{
    public const int Max = 100;

    public static string For(string tool, JsonElement input)
    {
        var raw = tool switch
        {
            "Bash" or "PowerShell" => Prop(input, "command"),
            "Read" or "Write" or "Edit" or "MultiEdit" => Prop(input, "file_path"),
            "NotebookEdit" => Prop(input, "notebook_path"),
            "Grep" or "Glob" => Prop(input, "pattern"),
            "WebFetch" => Prop(input, "url"),
            "WebSearch" => Prop(input, "query"),
            "Task" or "Agent" => Prop(input, "description"),
            _ => null,
        } ?? FirstString(input) ?? "";

        var lines = raw.Trim().Split('\n');
        var one = lines[0].TrimEnd('\r') + (lines.Length > 1 ? " …" : "");
        return one.Length <= Max ? one : one[..(Max - 1)] + "…";
    }

    /// <summary>Полный вход инструмента для карточки разрешения: оператор должен видеть всё, что
    /// разрешает, а не первую строку (ревью I-5).</summary>
    public const int MaxDetails = 4000;

    public static string Details(string tool, JsonElement input)
    {
        var text = tool switch
        {
            "Bash" or "PowerShell" => Prop(input, "command"),
            "Write" when Prop(input, "file_path") is { } path => $"{path}\n{Prop(input, "content")}",
            "Edit" when Prop(input, "file_path") is { } path =>
                $"{path}\n- {Prop(input, "old_string")}\n+ {Prop(input, "new_string")}",
            _ => null,
        } ?? (input.ValueKind == JsonValueKind.Undefined
            ? ""
            : JsonSerializer.Serialize(input, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        return text.Length <= MaxDetails ? text : text[..MaxDetails] + $"\n[обрезано: {text.Length} символов]";
    }

    private static string? Prop(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? FirstString(JsonElement o)
        => o.ValueKind != JsonValueKind.Object
            ? null
            : o.EnumerateObject().Select(p => p.Value).Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()).FirstOrDefault();
}
