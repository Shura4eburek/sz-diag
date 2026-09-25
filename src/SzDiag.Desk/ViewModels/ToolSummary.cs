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
