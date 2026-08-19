using SzDiag.Contracts;

namespace SzDiag.Cli;

public interface IHubApiClient
{
    Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken ct = default);
    Task<bool> CloseAsync(string sz, CancellationToken ct = default);

    /// <summary>Ручной шаг у машины в журнал СЗ. Принимается и когда сессии нет.</summary>
    Task<bool> AddNoteAsync(string sz, string text, CancellationToken ct = default);
    Task<TargetInfo?> GetTargetAsync(string sz, CancellationToken ct = default);
    Task<TriggerResult> TriggerTestAsync(string sz, string? filter, string? config,
        bool sameConfig, CancellationToken ct = default);
    Task<bool> TriggerDiagAsync(string sz, string? sections = null, CancellationToken ct = default);
    Task<ExecResult?> ExecAsync(string sz, string script, int? timeoutSeconds = null,
        CancellationToken ct = default, bool detached = false);
    Task<ExecJobStatus?> ExecStatusAsync(string sz, string jobId, int tailLines, CancellationToken ct = default);
    Task<PullResponse?> PullAsync(string sz, string path, long? maxBytes = null, bool recurse = false,
        CancellationToken ct = default);
    Task<PushResult?> PushAsync(string sz, string tool, CancellationToken ct = default);
    Task<ToolCatalogInfo?> GetToolsAsync(CancellationToken ct = default);
    Task<RebootTimeline?> GetRebootsAsync(string sz, CancellationToken ct = default);
    Task<bool> AddMaintenanceAsync(MaintenanceWindow window, CancellationToken ct = default);
    Task<IReadOnlyList<MaintenanceWindow>> GetMaintenanceAsync(string sz, CancellationToken ct = default);
}

/// <summary>Итог запуска прогона: hub возвращает текст причины, и CLI обязан его показать —
/// иначе подсказка про `--same-config` до пользователя не доедет.</summary>
public sealed record TriggerResult(bool Ok, string? Error);
