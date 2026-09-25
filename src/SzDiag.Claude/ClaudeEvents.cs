using System.Text.Json;

namespace SzDiag.Claude;

/// <summary>Событие сессии: пришедшее из stream-json `claude` или добавленное самим Desk
/// (сообщение пользователя, заметка, разрешение, падение процесса). Одна иерархия на оба
/// источника: лента и журнал сессии строятся из одного потока.</summary>
public abstract record ClaudeEvent
{
    /// <summary>Поле `timestamp` строки, если `claude` его прислал, иначе момент приёма.</summary>
    public DateTimeOffset? At { get; init; }
}

public sealed record McpServerStatus(string Name, string Status);

/// <summary>`system/init` — приходит на каждый ход, а не раз на процесс (спайк).</summary>
public sealed record SystemInit(string SessionId, string? Model, string? Cwd, string? PermissionMode,
    IReadOnlyList<string> Tools, IReadOnlyList<McpServerStatus> McpServers) : ClaudeEvent;

/// <param name="ParentToolUseId">Не null — событие субагента, а не главного хода.</param>
public sealed record AssistantText(string MessageId, string Text, string? ParentToolUseId) : ClaudeEvent;

public sealed record ToolUse(string MessageId, string Id, string Name, JsonElement Input, string? ParentToolUseId) : ClaudeEvent;

/// <param name="Denied">Инструмент не выполнялся — отказ в разрешении (`tool_result_meta.non_execution_kind`).</param>
public sealed record ToolResult(string ToolUseId, string Text, bool IsError, bool Denied, string? ParentToolUseId) : ClaudeEvent;

public sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheCreation)
{
    public static TokenUsage Zero { get; } = new(0, 0, 0, 0);

    public long Total => Input + Output + CacheRead + CacheCreation;

    public TokenUsage Add(TokenUsage o)
        => new(Input + o.Input, Output + o.Output, CacheRead + o.CacheRead, CacheCreation + o.CacheCreation);
}

/// <param name="Interrupted">Ход прерван control-запросом `interrupt` (`terminal_reason: aborted_streaming`).</param>
public sealed record TurnResult(string SessionId, bool IsError, bool Interrupted, string? Text,
    TokenUsage Usage, decimal CostUsd, long DurationMs, int NumTurns) : ClaudeEvent;

public sealed record ControlResponse(string RequestId, bool Success, string? Error) : ClaudeEvent;

/// <summary>Хуки, rate limit, счётчик thinking, фоновые задачи — в ленту не идут.</summary>
public sealed record ServiceEvent(string Type, string? Subtype) : ClaudeEvent;

/// <summary>Неизвестный тип не теряется: лента покажет свёрнутую карточку `raw`.</summary>
public sealed record UnknownEvent(string Type, string Raw) : ClaudeEvent;

/// <summary>Строка не разобралась — пропуск + лог (спека, «Ошибки»).</summary>
public sealed record ParseError(string Raw, string Message) : ClaudeEvent;

public sealed record DeskUserMessage(string Text) : ClaudeEvent;

public sealed record DeskNote(string Text) : ClaudeEvent;

public sealed record PermissionAsked(string RequestId, string ToolName, JsonElement Input, string? ToolUseId) : ClaudeEvent;

public sealed record PermissionAnswered(string RequestId, bool Allowed) : ClaudeEvent;

public sealed record ProcessCrashed(int? ExitCode, IReadOnlyList<string> StderrTail) : ClaudeEvent;
