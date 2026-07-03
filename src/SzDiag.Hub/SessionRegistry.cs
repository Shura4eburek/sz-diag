using System.Collections.Concurrent;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Потокобезопасный реестр активных сессий СЗ. Один экземпляр (singleton).</summary>
public sealed class SessionRegistry
{
    private sealed record Entry(SessionInfo Info, string ConnectionId);

    private readonly ConcurrentDictionary<string, Entry> _bySz = new();
    private readonly TimeProvider _time;

    public SessionRegistry(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public void Register(string sz, string ip, string hostname, string connectionId)
    {
        var now = _time.GetUtcNow();
        var info = new SessionInfo(sz, ip, hostname, SessionStatus.Online, now, now);
        _bySz[sz] = new Entry(info, connectionId);
    }

    public bool Heartbeat(string sz)
    {
        if (!_bySz.TryGetValue(sz, out var e)) return false;
        var now = _time.GetUtcNow();
        _bySz[sz] = e with { Info = e.Info with { Status = SessionStatus.Online, LastHeartbeat = now } };
        return true;
    }

    public bool SetActivity(string sz, string activity, DateTimeOffset? since)
    {
        if (!_bySz.TryGetValue(sz, out var e)) return false;
        _bySz[sz] = e with { Info = e.Info with { Activity = activity, ActivitySince = since } };
        return true;
    }

    public string? MarkOfflineByConnection(string connectionId)
    {
        foreach (var (sz, e) in _bySz)
        {
            if (e.ConnectionId != connectionId) continue;
            _bySz[sz] = e with { Info = e.Info with { Status = SessionStatus.Offline } };
            return sz;
        }
        return null;
    }

    /// <summary>Пометить офлайн сессии, чей heartbeat старше порога. Возвращает затронутые СЗ.</summary>
    public IReadOnlyList<string> MarkStaleOffline(TimeSpan maxAge)
    {
        var cutoff = _time.GetUtcNow() - maxAge;
        var affected = new List<string>();
        foreach (var (sz, e) in _bySz)
        {
            if (e.Info.Status == SessionStatus.Online && e.Info.LastHeartbeat < cutoff)
            {
                _bySz[sz] = e with { Info = e.Info with { Status = SessionStatus.Offline } };
                affected.Add(sz);
            }
        }
        return affected;
    }

    public void Remove(string sz) => _bySz.TryRemove(sz, out _);

    public string? TryGetConnectionId(string sz)
        => _bySz.TryGetValue(sz, out var e) ? e.ConnectionId : null;

    public IReadOnlyList<SessionInfo> GetActive()
        => _bySz.Values.Select(e => e.Info).ToList();
}
