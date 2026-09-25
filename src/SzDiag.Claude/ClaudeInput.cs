using System.Text.Json;

namespace SzDiag.Claude;

/// <summary>Строки stdin для `claude --input-format stream-json` (форма подтверждена спайком).</summary>
public static class ClaudeInput
{
    public static string UserMessage(string text)
        => JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } });

    /// <summary>Прерывание хода: процесс остаётся жив, ход закрывается `result` с
    /// `terminal_reason: aborted_streaming`.</summary>
    public static string Interrupt(string requestId)
        => JsonSerializer.Serialize(new { type = "control_request", request_id = requestId, request = new { subtype = "interrupt" } });
}
