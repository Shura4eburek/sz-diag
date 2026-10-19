using System.Text.Json;

namespace SzDiag.Claude;

/// <summary>Строка stdout `claude -p --output-format stream-json` → события. Схема — по фикстурам
/// спайка (docs/superpowers/specs/2026-09-25-desk-spike-notes.md). Блоки одного ответа приходят
/// отдельными строками с одинаковым `message.id`, поэтому строка может дать несколько событий.</summary>
public static class StreamJsonParser
{
    public static IReadOnlyList<ClaudeEvent> Parse(string line, DateTimeOffset receivedAt)
    {
        var text = line.TrimStart('\uFEFF').Trim();
        if (text.Length == 0) return Array.Empty<ClaudeEvent>();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new ClaudeEvent[] { new ParseError(line, ex.Message) { At = receivedAt } };
        }
        if (root.ValueKind != JsonValueKind.Object)
            return new ClaudeEvent[] { new ParseError(line, "строка — не JSON-объект") { At = receivedAt } };

        var type = Json.Str(root, "type") ?? "";
        var at = Json.Time(root) ?? receivedAt;
        IEnumerable<ClaudeEvent> events = type switch
        {
            "system" => new[] { SystemEvent(root) },
            "assistant" => Assistant(root),
            "user" => User(root),
            "result" => new[] { Result(root) },
            "control_response" => new[] { Control(root) },
            "rate_limit_event" => new ClaudeEvent[] { RateLimit(root) },
            _ when type.StartsWith(DeskLines.Prefix, StringComparison.Ordinal) => new[] { DeskLines.Parse(type, root) },
            _ => new ClaudeEvent[] { new UnknownEvent(type, text) },
        };
        return events.Select(e => e with { At = at }).ToList();
    }

    /// <summary>`rate_limit_info.unifiedWindows.{five_hour,seven_day}` — доля и время сброса (unix-секунды).</summary>
    private static ClaudeEvent RateLimit(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object)
            return new ServiceEvent("rate_limit_event", null);
        RateLimitWindow? Window(string name)
        {
            if (!info.TryGetProperty("unifiedWindows", out var w) || w.ValueKind != JsonValueKind.Object
                || !w.TryGetProperty(name, out var x) || x.ValueKind != JsonValueKind.Object
                || !x.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number)
                return null;
            return new RateLimitWindow(u.GetDouble(), DateTimeOffset.FromUnixTimeSeconds(Json.Long(x, "resetsAt")));
        }
        return new RateLimitUpdate(new RateLimitInfo(Json.Str(info, "status") ?? "", Window("five_hour"), Window("seven_day")));
    }

    private static ClaudeEvent SystemEvent(JsonElement root)
    {
        var subtype = Json.Str(root, "subtype");
        if (subtype != "init") return new ServiceEvent("system", subtype);

        var tools = root.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : new List<string>();
        var servers = root.TryGetProperty("mcp_servers", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Select(x => new McpServerStatus(Json.Str(x, "name") ?? "", Json.Str(x, "status") ?? "")).ToList()
            : new List<McpServerStatus>();
        return new SystemInit(Json.Str(root, "session_id") ?? "", Json.Str(root, "model"), Json.Str(root, "cwd"),
            Json.Str(root, "permissionMode"), tools, servers);
    }

    private static IEnumerable<ClaudeEvent> Assistant(JsonElement root)
    {
        var parent = Json.Str(root, "parent_tool_use_id");
        if (!root.TryGetProperty("message", out var msg)) yield break;
        var messageId = Json.Str(msg, "id") ?? "";
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;

        foreach (var block in content.EnumerateArray())
        {
            switch (Json.Str(block, "type"))
            {
                case "text" when Json.Str(block, "text") is { Length: > 0 } text:
                    yield return new AssistantText(messageId, text, parent);
                    break;
                case "tool_use":
                    var input = block.TryGetProperty("input", out var i) ? i.Clone() : default;
                    yield return new ToolUse(messageId, Json.Str(block, "id") ?? "", Json.Str(block, "name") ?? "", input, parent);
                    break;
                // thinking в режиме -p приходит пустым (только подпись) — показывать нечего.
            }
        }
    }

    private static IEnumerable<ClaudeEvent> User(JsonElement root)
    {
        var parent = Json.Str(root, "parent_tool_use_id");
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) yield break;

        var denied = DeniedIds(root);
        foreach (var block in content.EnumerateArray())
        {
            // Текстовые блоки («[Request interrupted by user]») — эхо, не событие ленты:
            // прерывание и так видно по result с terminal_reason.
            if (Json.Str(block, "type") != "tool_result") continue;
            var id = Json.Str(block, "tool_use_id") ?? "";
            yield return new ToolResult(id, ResultText(block), Json.Bool(block, "is_error"), denied.Contains(id), parent);
        }
    }

    private static HashSet<string> DeniedIds(JsonElement root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("tool_result_meta", out var meta) && meta.ValueKind == JsonValueKind.Array)
            foreach (var m in meta.EnumerateArray())
                if (Json.Str(m, "non_execution_kind") is not null && Json.Str(m, "id") is { } id)
                    ids.Add(id);
        return ids;
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return c.GetRawText();
        // MCP-инструменты отдают массив блоков: текст склеиваем, остальное помечаем типом.
        return string.Join("\n", c.EnumerateArray().Select(b =>
            Json.Str(b, "type") == "text" ? Json.Str(b, "text") ?? "" : $"[{Json.Str(b, "type")}]"));
    }

    private static ClaudeEvent Result(JsonElement root)
    {
        var usage = TokenUsage.Zero;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            usage = new TokenUsage(Json.Long(u, "input_tokens"), Json.Long(u, "output_tokens"),
                Json.Long(u, "cache_read_input_tokens"), Json.Long(u, "cache_creation_input_tokens"));
        var cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetDecimal()
            : 0m;
        return new TurnResult(Json.Str(root, "session_id") ?? "", Json.Bool(root, "is_error"),
            Json.Str(root, "terminal_reason") == "aborted_streaming", Json.Str(root, "result"),
            usage, cost, Json.Long(root, "duration_ms"), (int)Json.Long(root, "num_turns"));
    }

    private static ClaudeEvent Control(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var r)) return new ControlResponse("", false, "нет поля response");
        return new ControlResponse(Json.Str(r, "request_id") ?? "", Json.Str(r, "subtype") == "success", Json.Str(r, "error"));
    }
}
