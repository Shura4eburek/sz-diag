using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Персистенс сессий: активные + история открытий/закрытий.</summary>
public interface ISessionStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task RecordOpenAsync(SessionRecord record, CancellationToken ct = default);
    Task RecordCloseAsync(string sz, DateTimeOffset closedAt, CancellationToken ct = default);
    Task<IReadOnlyList<SessionRecord>> GetHistoryAsync(CancellationToken ct = default);
}
