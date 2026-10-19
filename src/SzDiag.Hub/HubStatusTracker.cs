using SzDiag.Contracts;
using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>Живое состояние фоновых служб hub для `GET /api/status`: бэкап kb и туннель сами
/// отчитываются сюда, эндпоинт только читает. In-memory: после рестарта hub «прогона ещё не было».</summary>
public sealed class HubStatusTracker(TimeProvider time)
{
    private readonly object _gate = new();
    private KbBackupStatus _kb = new(false, null, null, null);
    private TunnelStatus _tunnel = new(TunnelStates.Off, null);

    public void KbBackupEnabled(bool enabled)
    {
        lock (_gate) _kb = _kb with { Enabled = enabled };
    }

    public void KbBackupRan(KbBackupResult r)
    {
        lock (_gate) _kb = _kb with { LastRunAt = time.GetUtcNow(), Outcome = r.Outcome.ToString(), Message = r.Message };
    }

    public void KbBackupCrashed(string message)
    {
        lock (_gate)
            _kb = _kb with { LastRunAt = time.GetUtcNow(), Outcome = nameof(KbBackupOutcome.Failed), Message = message };
    }

    public void Tunnel(string state)
    {
        lock (_gate) _tunnel = new TunnelStatus(state, time.GetUtcNow());
    }

    public HubStatus Snapshot(string? agentPackageVersion)
    {
        lock (_gate) return new HubStatus(agentPackageVersion, _kb, _tunnel);
    }
}
