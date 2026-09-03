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

        // Сессия могла уже быть офлайн (агент откатился ярлыком, канал мёртв) — ждать итог
        // тогда бессмысленно, он никогда не придёт (бэклог п.119).
        var wasOnline = _registry.GetActive().Any(s => s.Sz == sz && s.Status == SessionStatus.Online);

        _revertResults.Remove(sz);   // не подхватить сводку от прошлой сессии этой же СЗ
        await _sender.SendRevertAsync(connId, sz, ct);

        RevertResult? revert = null;
        if (wasOnline)
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
        return new CloseOutcome(true, revert);
    }
}
