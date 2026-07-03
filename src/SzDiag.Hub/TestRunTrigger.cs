namespace SzDiag.Hub;

/// <summary>Триггерит прогон тестов на агенте по номеру СЗ (push RunTests).</summary>
public sealed class TestRunTrigger
{
    private readonly SessionRegistry _registry;
    private readonly IAgentCommandSender _sender;

    public TestRunTrigger(SessionRegistry registry, IAgentCommandSender sender)
    {
        _registry = registry;
        _sender = sender;
    }

    public async Task<bool> TriggerAsync(string sz, string? filter = null, CancellationToken ct = default)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return false;
        await _sender.SendRunTestsAsync(connId, sz, filter, ct);
        return true;
    }
}
