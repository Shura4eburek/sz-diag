using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Персистенс сессий: активные + история открытий/закрытий.</summary>
public interface ISessionStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task RecordOpenAsync(SessionRecord record, CancellationToken ct = default);
    Task RecordCloseAsync(string sz, DateTimeOffset closedAt, CancellationToken ct = default);
    Task<IReadOnlyList<SessionRecord>> GetHistoryAsync(CancellationToken ct = default);

    /// <summary>Зафиксировать вырубон/перезагрузку клиента (смена boot-time).</summary>
    Task RecordRebootAsync(RebootEvent evt, CancellationToken ct = default);

    /// <summary>Таймлайн вырубонов по СЗ, от старых к новым.</summary>
    Task<RebootTimeline> GetRebootsAsync(string sz, CancellationToken ct = default);
}
