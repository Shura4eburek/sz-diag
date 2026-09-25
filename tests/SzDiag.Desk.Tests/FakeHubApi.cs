using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.Tests;

public sealed class FakeHubApi : IHubApiClient
{
    public Func<IReadOnlyList<SessionInfo>> Sessions { get; set; } = () => Array.Empty<SessionInfo>();
    public Func<IReadOnlyList<TransferInfo>> Transfers { get; set; } = () => Array.Empty<TransferInfo>();
    public Func<HealthzResponse?> Health { get; set; } = () => null;
    public Func<string?> Version { get; set; } = () => "1.14";

    public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken ct = default) => Task.FromResult(Sessions());
    public Task<IReadOnlyList<TransferInfo>> GetTransfersAsync(CancellationToken ct = default) => Task.FromResult(Transfers());
    public Task<HealthzResponse?> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(Health());
    public Task<string?> GetHubVersionAsync(CancellationToken ct = default) => Task.FromResult(Version());

    // Остальное Desk в части 1 не зовёт.
    private static Task<T> No<T>() => throw new NotSupportedException();
    public Task<CloseOutcome> CloseAsync(string sz, CancellationToken ct = default) => No<CloseOutcome>();
    public Task<NoteResult> AddNoteAsync(string sz, string text, CancellationToken ct = default) => No<NoteResult>();
    public Task<TargetInfo?> GetTargetAsync(string sz, CancellationToken ct = default) => No<TargetInfo?>();
    public Task<TriggerResult> TriggerTestAsync(string sz, string? filter, string? config, bool sameConfig,
        string? schedule = null, CancellationToken ct = default) => No<TriggerResult>();
    public Task<OcctSchedulePlan?> GetOcctScheduleAsync(string? profile = null, CancellationToken ct = default) => No<OcctSchedulePlan?>();
    public Task<OcctReportSummary?> GetTestResultAsync(string sz, CancellationToken ct = default) => No<OcctReportSummary?>();
    public Task<bool> TriggerDiagAsync(string sz, string? sections = null, CancellationToken ct = default) => No<bool>();
    public Task<ExecResult?> ExecAsync(string sz, string script, int? timeoutSeconds = null, CancellationToken ct = default,
        bool detached = false, bool isolated = false, bool asSystem = false) => No<ExecResult?>();
    public Task<ExecJobStatus?> ExecStatusAsync(string sz, string jobId, int tailLines, CancellationToken ct = default) => No<ExecJobStatus?>();
    public Task<ExecJobStatus?> ExecCancelAsync(string sz, string jobId, CancellationToken ct = default) => No<ExecJobStatus?>();
    public Task<ExecJobStatus?> ExecJobsAsync(string sz, CancellationToken ct = default) => No<ExecJobStatus?>();
    public Task<PullResponse?> PullAsync(string sz, string path, long? maxBytes = null, bool recurse = false,
        string? label = null, CancellationToken ct = default) => No<PullResponse?>();
    public Task<PushResult?> PushAsync(string sz, string tool, CancellationToken ct = default) => No<PushResult?>();
    public Task<ToolCatalogInfo?> GetToolsAsync(CancellationToken ct = default) => No<ToolCatalogInfo?>();
    public Task<RebootTimeline?> GetRebootsAsync(string sz, CancellationToken ct = default) => No<RebootTimeline?>();
    public Task<bool> AddMaintenanceAsync(MaintenanceWindow window, CancellationToken ct = default) => No<bool>();
    public Task<IReadOnlyList<MaintenanceWindow>> GetMaintenanceAsync(string sz, CancellationToken ct = default) => No<IReadOnlyList<MaintenanceWindow>>();
    public Task<RestartAgentOutcome> RestartAgentAsync(string sz, CancellationToken ct = default) => No<RestartAgentOutcome>();
}
