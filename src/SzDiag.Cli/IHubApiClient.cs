using SzDiag.Contracts;

namespace SzDiag.Cli;

public interface IHubApiClient
{
    Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken ct = default);
    Task<bool> CloseAsync(string sz, CancellationToken ct = default);
    Task<TargetInfo?> GetTargetAsync(string sz, CancellationToken ct = default);
    Task<bool> TriggerTestAsync(string sz, string? filter = null, CancellationToken ct = default);
    Task<bool> TriggerDiagAsync(string sz, string? sections = null, CancellationToken ct = default);
}
