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

    /// <summary>Регистрация (или переподключение) агента. Если у СЗ уже был известен boot-time
    /// и пришёл другой — значит клиент реально перезагрузился: фиксируем момент в
    /// <see cref="SessionInfo.LastRebootAt"/> и возвращаем true. Это единственный надёжный
    /// признак ребута: пропажа heartbeat под нагрузкой ребутом не является.</summary>
    public bool Register(string sz, string ip, string hostname, string connectionId,
        DateTimeOffset? bootTime = null)
    {
        var now = _time.GetUtcNow();
        var rebooted = _bySz.TryGetValue(sz, out var prev)
                       && prev.Info.BootTime is { } was
                       && bootTime is { } isNow
                       && was != isNow;
        var lastReboot = rebooted ? now : (_bySz.TryGetValue(sz, out var p) ? p.Info.LastRebootAt : null);
        var info = new SessionInfo(sz, ip, hostname, SessionStatus.Online, now, now,
            BootTime: bootTime, LastRebootAt: lastReboot);
        _bySz[sz] = new Entry(info, connectionId);
        return rebooted;
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
        var now = _time.GetUtcNow();
        _bySz[sz] = e with { Info = e.Info with { Activity = activity, ActivitySince = since, Status = SessionStatus.Online, LastHeartbeat = now } };
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
