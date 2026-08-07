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

    /// <summary>Слить события из журнала клиента с уже известными. Возвращает, сколько
    /// записей добавилось: те, что hub уже видел сам (по времени ±5 минут), не дублируются
    /// (бэклог п.97).</summary>
    Task<int> MergeJournalEventsAsync(PowerEventsReport report, CancellationToken ct = default);
}
