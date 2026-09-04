using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>
/// Закрытие СЗ по команде из CLI: шлёт агенту revert (если известен connectionId),
/// фиксирует закрытие в истории и убирает сессию из реестра. Идемпотентно на уровне
/// «неизвестная СЗ = false».
/// </summary>
public sealed class SessionCloser
{
    private readonly SessionRegistry _registry;
    private readonly ISessionStore _store;
    private readonly IAgentCommandSender _sender;
    private readonly RevertResultStore _revertResults;
    private readonly TimeSpan _revertWait;

    /// <param name="revertWait">Сколько ждать итог отката от живого агента, прежде чем
    /// вернуть close без него (агент обычно откатывается за секунды — в основном Remove-*
    /// вызовы). По офлайн-СЗ не ждём вовсе: агент уже не ответит, а закрытие мёртвой сессии
    /// не должно висеть впустую.</param>
    public SessionCloser(SessionRegistry registry, ISessionStore store, IAgentCommandSender sender,
        RevertResultStore revertResults, TimeSpan? revertWait = null)
    {
        _registry = registry;
        _store = store;
        _sender = sender;
        _revertResults = revertResults;
        _revertWait = revertWait ?? TimeSpan.FromSeconds(4);
    }

    public async Task<CloseOutcome> CloseAsync(string sz, CancellationToken ct = default)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return new CloseOutcome(false, null);

        var info = _registry.TryGetInfo(sz);
        // Сессия могла уже быть офлайн (агент откатился ярлыком, канал мёртв) — ждать итог
        // тогда бессмысленно, он никогда не придёт (бэклог п.119).
        var wasOnline = info?.Status == SessionStatus.Online;

        // Агент мог прислать сводку САМ ещё до этого close (self-revert по клавише C, пока
        // канал был жив) — подхватываем её вместо того, чтобы ждать заново. Отбрасываем
        // только заведомо устаревшую: от прошлой сессии этой же СЗ (Critical-2, ревью волны 1
        // — раньше здесь стоял безусловный Remove ДО отправки revert, и единственный сценарий,
        // ради которого #55 делался — агент уже офлайн к моменту close — гарантированно терял
        // сводку).
        var pending = info is not null ? _revertResults.TryGetFresh(sz, info.ConnectedAt) : null;

        await _sender.SendRevertAsync(connId, sz, ct);

        var revert = pending;
        if (revert is null && wasOnline)
        {
            var deadline = DateTime.UtcNow + _revertWait;
            while (DateTime.UtcNow < deadline)
            {
                revert = _revertResults.TryGet(sz);
                if (revert is not null) break;
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }
        }

        await _store.RecordCloseAsync(sz, DateTimeOffset.UtcNow, ct);
        _registry.Remove(sz);
        _revertResults.Remove(sz);
        return new CloseOutcome(true, revert);
    }
}
