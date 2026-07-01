namespace SzDiag.Agent;

/// <summary>Оркестрация сессии агента: открыть доступ, подключиться, регистрировать,
/// слать heartbeat, идемпотентно откатывать по любому триггеру.</summary>
public sealed class AgentSession
{
    private readonly ISystemAccessManager _manager;
    private readonly IHubLink _link;
    private readonly AccessSpec _spec;
    private readonly string _hostname;
    private readonly RevertCoordinator _coordinator;
    private RevertState? _state;

    public AgentSession(ISystemAccessManager manager, IHubLink link, AccessSpec spec, string hostname)
    {
        _manager = manager;
        _link = link;
        _spec = spec;
        _hostname = hostname;
        _coordinator = new RevertCoordinator(DoRevertAsync);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _state = _manager.Open(_spec);
        _link.OnRevert(async _ => await _coordinator.TriggerAsync());
        await _link.ConnectAsync(ct);
        await _link.RegisterAsync(_spec.Sz, _hostname, ct);
    }

    public Task HeartbeatOnceAsync(CancellationToken ct = default) => _link.HeartbeatAsync(_spec.Sz, ct);

    /// <summary>Локальный/watchdog/консоль-триггер отката.</summary>
    public Task RevertAsync() => _coordinator.TriggerAsync();

    private async Task DoRevertAsync()
    {
        if (_state is not null) _manager.Revert(_state);
        await _link.DisposeAsync();
    }
}
