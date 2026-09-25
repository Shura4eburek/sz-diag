using System.Globalization;
using System.Text.Json;

namespace SzDiag.Claude;

/// <summary>Строки журнала сессии, которые пишет сам Desk (тип `desk_*`). Журнал — сырой
/// stream-json вперемешку с ними, и читается тем же парсером: `--resume` историю в stdout не
/// повторяет, так что ленту после перезапуска Desk восстанавливает только этот журнал.</summary>
public static class DeskLines
{
    public const string Prefix = "desk_";

    public static string? Serialize(ClaudeEvent e)
    {
        var at = (e.At ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);
        object? line = e switch
        {
            DeskUserMessage m => new { type = "desk_user", text = m.Text, timestamp = at },
            DeskNote n => new { type = "desk_note", text = n.Text, timestamp = at },
            PermissionAsked p => new
            {
                type = "desk_permission_asked", request_id = p.RequestId, tool_name = p.ToolName,
                input = p.Input.ValueKind == JsonValueKind.Undefined ? (object?)null : p.Input,
                tool_use_id = p.ToolUseId, timestamp = at,
            },
            PermissionAnswered a => new { type = "desk_permission_answered", request_id = a.RequestId, allowed = a.Allowed, timestamp = at },
            ProcessCrashed c => new { type = "desk_crash", exit_code = c.ExitCode, stderr = c.StderrTail, timestamp = at },
            _ => null,
        };
        return line is null ? null : JsonSerializer.Serialize(line);
    }

    internal static ClaudeEvent Parse(string type, JsonElement root) => type switch
    {
        "desk_user" => new DeskUserMessage(Json.Str(root, "text") ?? ""),
        "desk_note" => new DeskNote(Json.Str(root, "text") ?? ""),
        "desk_permission_asked" => new PermissionAsked(Json.Str(root, "request_id") ?? "", Json.Str(root, "tool_name") ?? "",
            root.TryGetProperty("input", out var i) ? i.Clone() : default, Json.Str(root, "tool_use_id")),
        "desk_permission_answered" => new PermissionAnswered(Json.Str(root, "request_id") ?? "", Json.Bool(root, "allowed")),
        "desk_crash" => new ProcessCrashed(
            root.TryGetProperty("exit_code", out var c) && c.ValueKind == JsonValueKind.Number ? (int?)c.GetInt32() : null,
            root.TryGetProperty("stderr", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : new List<string>()),
        _ => new UnknownEvent(type, root.GetRawText()),
    };
}
