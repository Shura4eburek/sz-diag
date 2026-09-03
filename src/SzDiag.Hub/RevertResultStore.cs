using System.Collections.Concurrent;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Держит последний присланный агентом итог отката по СЗ, до следующего запроса.
/// `close` печатает его, если агент успел прислать сводку до отключения канала — без этого
/// «доступ закрыт полностью» приходилось подтверждать походом к машине (бэклог п.119).</summary>
public sealed class RevertResultStore
{
    private readonly ConcurrentDictionary<string, RevertResult> _bySz = new();

    public void Set(RevertResult result) => _bySz[result.Sz] = result;

    public RevertResult? TryGet(string sz) => _bySz.TryGetValue(sz, out var r) ? r : null;

    public void Remove(string sz) => _bySz.TryRemove(sz, out _);
}
