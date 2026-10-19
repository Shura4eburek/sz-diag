namespace SzDiag.Contracts;

/// <summary>`GET /api/status` — то, что статусбар Desk показывает помимо /healthz: какой пакет
/// агента раздаёт hub, когда последний раз ушёл бэкап kb, жив ли туннель (спека 2026-09-25).
/// Раньше всё это видно было только в консоли hub.</summary>
/// <param name="AgentPackageVersion">Содержимое agent-dist/version.txt; null — пакета нет.</param>
public sealed record HubStatus(string? AgentPackageVersion, KbBackupStatus KbBackup, TunnelStatus Tunnel);

/// <param name="Outcome">Имя исхода последнего прогона (`NoChanges`, `Pushed`, `CommittedNotPushed`,
/// `Failed`) строкой — Contracts не ссылается на SzDiag.Kb; null — прогона ещё не было.</param>
public sealed record KbBackupStatus(bool Enabled, DateTimeOffset? LastRunAt, string? Outcome, string? Message);

/// <param name="State">Одно из <see cref="TunnelStates"/>.</param>
/// <param name="Since">С какого момента туннель в этом состоянии.</param>
public sealed record TunnelStatus(string State, DateTimeOffset? Since);

public static class TunnelStates
{
    public const string Off = "off";
    public const string NotFound = "not-found";
    public const string Running = "running";
    public const string Restarting = "restarting";
}

public static class HubStatusRoutes
{
    public const string Status = "/api/status";
}
