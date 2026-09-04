using SzDiag.Contracts;

namespace SzDiag.Cli;

public interface IHubApiClient
{
    Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken ct = default);
    Task<CloseOutcome> CloseAsync(string sz, CancellationToken ct = default);

    /// <summary>Ручной шаг у машины в журнал СЗ. Принимается и когда сессии нет.</summary>
    Task<NoteResult> AddNoteAsync(string sz, string text, CancellationToken ct = default);
    Task<TargetInfo?> GetTargetAsync(string sz, CancellationToken ct = default);
    Task<TriggerResult> TriggerTestAsync(string sz, string? filter, string? config,
        bool sameConfig, CancellationToken ct = default);
    Task<bool> TriggerDiagAsync(string sz, string? sections = null, CancellationToken ct = default);
    /// <param name="isolated">Только вместе с <paramref name="detached"/>: обернуть фоновую
    /// задачу транзиентной scheduled task под SYSTEM вместо дочернего процесса агента — дерево
    /// процессов переживает падение агента (бэклог п.53).</param>
    /// <param name="asSystem">Синхронный запуск под SYSTEM (та же транзиентная scheduled task,
    /// что у sshd) — часть операций (задачи `UpdateOrchestrator`, объекты TrustedInstaller)
    /// упирается в Access denied даже под админом (бэклог п.39). Игнорируется вместе с
    /// <paramref name="detached"/> — там для SYSTEM уже есть <paramref name="isolated"/>.</param>
    Task<ExecResult?> ExecAsync(string sz, string script, int? timeoutSeconds = null,
        CancellationToken ct = default, bool detached = false, bool isolated = false, bool asSystem = false);
    Task<ExecJobStatus?> ExecStatusAsync(string sz, string jobId, int tailLines, CancellationToken ct = default);

    /// <summary>Снять фоновую exec-задачу. null — СЗ не онлайн (бэклог п.134/172/176).</summary>
    Task<ExecJobStatus?> ExecCancelAsync(string sz, string jobId, CancellationToken ct = default);

    /// <summary>Список фоновых exec-задач на агенте (сводка в Tail).</summary>
    Task<ExecJobStatus?> ExecJobsAsync(string sz, CancellationToken ct = default);
    Task<PullResponse?> PullAsync(string sz, string path, long? maxBytes = null, bool recurse = false,
        CancellationToken ct = default);
    Task<PushResult?> PushAsync(string sz, string tool, CancellationToken ct = default);
    Task<ToolCatalogInfo?> GetToolsAsync(CancellationToken ct = default);
    Task<RebootTimeline?> GetRebootsAsync(string sz, CancellationToken ct = default);
    Task<bool> AddMaintenanceAsync(MaintenanceWindow window, CancellationToken ct = default);
    Task<IReadOnlyList<MaintenanceWindow>> GetMaintenanceAsync(string sz, CancellationToken ct = default);

    /// <summary>Версия/дата сборки hub — null, если hub не ответил (протух молча — бэклог п.165).</summary>
    Task<string?> GetHubVersionAsync(CancellationToken ct = default);
}

/// <summary>Итог запуска прогона: hub возвращает текст причины, и CLI обязан его показать —
/// иначе подсказка про `--same-config` до пользователя не доедет.</summary>
public sealed record TriggerResult(bool Ok, string? Error);

/// <summary>Итог `AddNoteAsync`. Эндпоинт журнала принимает любую валидную СЗ без проверки
/// существования сессии — 404 там означает не «СЗ не найдена», а «hub не знает такой
/// маршрут вообще», то есть hub старее CLI. Раньше оба случая давали одинаковое безликое
/// «hub не принял заметку», и на живой заявке (161190) причину пришлось искать вручную
/// (бэклог п.191).</summary>
public enum NoteResult { Ok, Rejected, HubTooOld }
