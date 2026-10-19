using System.Collections.Concurrent;

namespace SzDiag.Claude;

/// <summary>Правило «1 ключ = 1 сессия» и маршрутизация разрешений по ключу. Объект сессии
/// поднимается лениво по записи реестра; процесс — только при первом сообщении.</summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ClaudeSession> _sessions = new(StringComparer.Ordinal);
    private readonly SessionDeps _d;

    public SessionManager(SessionDeps deps)
    {
        _d = deps;
        deps.Broker.Requested += p =>
        {
            if (Get(p.Key) is { } s) s.OnPermissionAsked(p);
            // Ключа нет в Desk — спросить некого; молчание повесило бы ход навсегда.
            else deps.Broker.Resolve(p.RequestId, false, "нет сессии Desk для этого ключа");
        };
        deps.Broker.Resolved += (id, allowed) =>
        {
            foreach (var s in _sessions.Values) s.OnPermissionAnswered(id, allowed);
        };
    }

    public IReadOnlyList<SessionRecord> Records => _d.Index.All;

    /// <summary>«Начать сессию»: запись в реестре без session_id; повторный вызов — та же сессия
    /// (и тот же профиль: он закреплён за разговором).</summary>
    public ClaudeSession Create(string key, string? profile = null, string? workDir = null)
    {
        if (_d.Index.Get(key) is null) _d.Index.Put(new SessionRecord(key, null, _d.Time.GetUtcNow(), false, profile, workDir));
        return _sessions.GetOrAdd(key, k => new ClaudeSession(k, _d));
    }

    public ClaudeSession? Get(string key)
    {
        if (_sessions.TryGetValue(key, out var s)) return s;
        return _d.Index.Get(key) is null ? null : _sessions.GetOrAdd(key, k => new ClaudeSession(k, _d));
    }

    /// <summary>Только уже поднятые — без чтения журнала с диска (для частых опросов из UI).</summary>
    public ClaudeSession? Peek(string key) => _sessions.TryGetValue(key, out var s) ? s : null;

    public Task StopAllAsync() => Task.WhenAll(_sessions.Values.Select(s => s.StopAsync()));

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}
