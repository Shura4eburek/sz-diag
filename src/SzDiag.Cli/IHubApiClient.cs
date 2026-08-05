using SzDiag.Contracts;

namespace SzDiag.Cli;

public interface IHubApiClient
{
    Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken ct = default);
    Task<bool> CloseAsync(string sz, CancellationToken ct = default);
    Task<TargetInfo?> GetTargetAsync(string sz, CancellationToken ct = default);
    Task<bool> TriggerTestAsync(string sz, string? filter = null, CancellationToken ct = default);
    Task<bool> TriggerDiagAsync(string sz, string? sections = null, CancellationToken ct = default);
    Task<ExecResult?> ExecAsync(string sz, string script, int? timeoutSeconds = null,
        CancellationToken ct = default, bool detached = false);
    Task<ExecJobStatus?> ExecStatusAsync(string sz, string jobId, int tailLines, CancellationToken ct = default);
    Task<PullResponse?> PullAsync(string sz, string path, long? maxBytes = null, CancellationToken ct = default);
    Task<PushResult?> PushAsync(string sz, string tool, CancellationToken ct = default);
    Task<IReadOnlyList<ToolInfo>> GetToolsAsync(CancellationToken ct = default);
    Task<RebootTimeline?> GetRebootsAsync(string sz, CancellationToken ct = default);
}
