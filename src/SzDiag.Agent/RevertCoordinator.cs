namespace SzDiag.Agent;

/// <summary>Гарантирует, что откат выполнится ровно один раз при любом числе триггеров.</summary>
public sealed class RevertCoordinator
{
    private readonly Func<Task> _action;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _done;

    public RevertCoordinator(Func<Task> action) => _action = action;

    public async Task TriggerAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_done) return;
            _done = true;
            await _action();
        }
        finally
        {
            _gate.Release();
        }
    }
}
