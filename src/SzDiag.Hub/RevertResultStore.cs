using System.Collections.Concurrent;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Держит последний присланный агентом итог отката по СЗ, до следующего запроса.
/// `close` печатает его, если агент успел прислать сводку до отключения канала — без этого
/// «доступ закрыт полностью» приходилось подтверждать походом к машине (бэклог п.119).</summary>
public sealed class RevertResultStore
{
    private readonly ConcurrentDictionary<string, (RevertResult Result, DateTimeOffset At)> _bySz = new();
    private readonly TimeProvider _time;

    public RevertResultStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public void Set(RevertResult result) => _bySz[result.Sz] = (result, _time.GetUtcNow());

    public RevertResult? TryGet(string sz) => _bySz.TryGetValue(sz, out var e) ? e.Result : null;

    /// <summary>Сводка, полученная не раньше <paramref name="notBefore"/> — отсекает только
    /// заведомо устаревшую сводку от ПРОШЛОЙ сессии этой же СЗ, а не любую (Critical-2, ревью
    /// волны 1): раньше `SessionCloser` безусловно стирал сводку до отправки revert, и
    /// единственный сценарий, ради которого #55 делался — агент уже офлайн к моменту close —
    /// гарантированно её терял.</summary>
    public RevertResult? TryGetFresh(string sz, DateTimeOffset notBefore)
        => _bySz.TryGetValue(sz, out var e) && e.At >= notBefore ? e.Result : null;

    public void Remove(string sz) => _bySz.TryRemove(sz, out _);
}
