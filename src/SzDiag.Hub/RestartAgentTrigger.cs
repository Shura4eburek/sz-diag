namespace SzDiag.Hub;

/// <summary>Перезапуск агента по номеру СЗ — отдельным SignalR-путём от Exec (бэклог п.202/п.215):
/// раньше `agent restart` уходил как обычный exec-скрипт и потому был бесполезен ровно тогда,
/// когда нужен — сам ходил через тот же ack/очередь, что и всё остальное на забитом канале.
/// Fire-and-forget, как Revert: подтверждения ждать нечем, агент сам себя не убивает
/// (регистрирует отложенную задачу и продолжает жить, пока она не сработает).</summary>
public sealed class RestartAgentTrigger
{
    private readonly SessionRegistry _registry;
    private readonly IAgentCommandSender _sender;

    public RestartAgentTrigger(SessionRegistry registry, IAgentCommandSender sender)
    {
        _registry = registry;
        _sender = sender;
    }

    /// <summary>false — СЗ не онлайн (нечего перезапускать).</summary>
    public async Task<bool> TriggerAsync(string sz, CancellationToken ct = default)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return false;
        await _sender.SendRestartAgentAsync(connId, sz, ct);
        return true;
    }
}
