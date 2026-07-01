namespace SzDiag.Hub;

/// <summary>
/// Закрытие СЗ по команде из CLI: шлёт агенту revert (если известен connectionId),
/// фиксирует закрытие в истории и убирает сессию из реестра. Идемпотентно на уровне
/// «неизвестная СЗ = false».
/// </summary>
public sealed class SessionCloser
{
    private readonly SessionRegistry _registry;
    private readonly ISessionStore _store;
    private readonly IAgentCommandSender _sender;

    public SessionCloser(SessionRegistry registry, ISessionStore store, IAgentCommandSender sender)
    {
        _registry = registry;
        _store = store;
        _sender = sender;
    }

    public async Task<bool> CloseAsync(string sz, CancellationToken ct = default)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return false;

        await _sender.SendRevertAsync(connId, sz, ct);
        await _store.RecordCloseAsync(sz, DateTimeOffset.UtcNow, ct);
        _registry.Remove(sz);
        return true;
    }
}
