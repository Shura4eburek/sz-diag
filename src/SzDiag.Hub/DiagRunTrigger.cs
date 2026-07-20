namespace SzDiag.Hub;

/// <summary>Триггерит диагностику на агенте по номеру СЗ (push RunDiag). Секции опциональны:
/// null/пусто — весь каталог, иначе список секций через запятую.</summary>
public sealed class DiagRunTrigger
{
    private readonly SessionRegistry _registry;
    private readonly IAgentCommandSender _sender;

    public DiagRunTrigger(SessionRegistry registry, IAgentCommandSender sender)
    {
        _registry = registry;
        _sender = sender;
    }

    public async Task<bool> TriggerAsync(string sz, string? sections = null, CancellationToken ct = default)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return false;
        await _sender.SendRunDiagAsync(connId, sz, sections, ct);
        return true;
    }
}
