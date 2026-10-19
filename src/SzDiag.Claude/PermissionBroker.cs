using System.Collections.Concurrent;
using System.Text.Json;

namespace SzDiag.Claude;

public sealed record PendingPermission(string RequestId, string Key, string ToolName, JsonElement Input,
    string? ToolUseId, DateTimeOffset At);

/// <summary>Ответ для `--permission-prompt-tool` (схема — спайк).</summary>
public sealed record PermissionVerdict(bool Allow, JsonElement? UpdatedInput, string? Message)
{
    public string ToJson() => Allow
        ? JsonSerializer.Serialize(new { behavior = "allow", updatedInput = UpdatedInput })
        : JsonSerializer.Serialize(new { behavior = "deny", message = Message ?? "отклонено" });
}

/// <summary>Запросы разрешений от всех сессий: MCP-тулза ждёт здесь решения человека в окне.
/// Ждём сколько угодно, как терминал (спека): отмена приходит только от самого claude
/// (обрыв запроса) или от остановки сессии.</summary>
public sealed class PermissionBroker(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (PendingPermission P, TaskCompletionSource<PermissionVerdict> Tcs)> _pending = new();

    public event Action<PendingPermission>? Requested;

    /// <summary>Решение принято: requestId, разрешено ли.</summary>
    public event Action<string, bool>? Resolved;

    public int PendingCount => _pending.Count;

    public IReadOnlyList<PendingPermission> Pending(string key)
        => _pending.Values.Select(e => e.P).Where(p => p.Key == key).OrderBy(p => p.At).ToList();

    public async Task<PermissionVerdict> AskAsync(string key, string toolName, JsonElement input, string? toolUseId,
        CancellationToken ct)
    {
        var p = new PendingPermission(Guid.NewGuid().ToString("N"), key, toolName, input.Clone(), toolUseId, time.GetUtcNow());
        var tcs = new TaskCompletionSource<PermissionVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[p.RequestId] = (p, tcs);
        using var reg = ct.Register(() => Resolve(p.RequestId, false, "запрос отменён"));
        Requested?.Invoke(p);
        return await tcs.Task.ConfigureAwait(false);
    }

    public bool Resolve(string requestId, bool allow, string? message = null)
    {
        if (!_pending.TryRemove(requestId, out var e)) return false;
        e.Tcs.TrySetResult(allow
            ? new PermissionVerdict(true, e.P.Input, null)
            : new PermissionVerdict(false, null, message ?? "отклонено оператором"));
        Resolved?.Invoke(requestId, allow);
        return true;
    }

    public void DenyAll(string key, string message)
    {
        foreach (var (id, e) in _pending)
            if (e.P.Key == key) Resolve(id, false, message);
    }
}
