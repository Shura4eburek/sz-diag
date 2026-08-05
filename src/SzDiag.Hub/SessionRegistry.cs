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

    /// <summary>Что произошло при регистрации агента.</summary>
    /// <param name="Rebooted">Boot-time сменился — машина реально перезагрузилась.</param>
    /// <param name="PreviousBootTime">Прежний boot-time (для записи события).</param>
    /// <param name="UptimeBefore">Сколько машина продержалась до вырубона.</param>
    /// <param name="ActivityBefore">Чем была занята — «продержалась N минут под тестом».</param>
    public sealed record RegisterOutcome(
        bool Rebooted,
        DateTimeOffset? PreviousBootTime = null,
        TimeSpan? UptimeBefore = null,
        string? ActivityBefore = null);

    /// <summary>Регистрация (или переподключение) агента. Если у СЗ уже был известен boot-time
    /// и пришёл другой — значит клиент реально перезагрузился: фиксируем момент в
    /// <see cref="SessionInfo.LastRebootAt"/>, считаем ребуты и отдаём подробности вызывающему.
    /// Это единственный надёжный признак ребута: пропажа heartbeat под нагрузкой ребутом не
    /// является, а ICMP у типовой клиентской винды закрыт из коробки.</summary>
    public RegisterOutcome Register(string sz, string ip, string hostname, string connectionId,
        DateTimeOffset? bootTime = null)
    {
        var now = _time.GetUtcNow();
        _bySz.TryGetValue(sz, out var prev);

        var rebooted = prev is not null
                       && prev.Info.BootTime is { } was
                       && bootTime is { } isNow
                       && was != isNow;

        var lastReboot = rebooted ? now : prev?.Info.LastRebootAt;
        var rebootCount = (prev?.Info.RebootCount ?? 0) + (rebooted ? 1 : 0);
        var info = new SessionInfo(sz, ip, hostname, SessionStatus.Online, now, now,
            BootTime: bootTime, LastRebootAt: lastReboot, RebootCount: rebootCount);
        _bySz[sz] = new Entry(info, connectionId);

        if (!rebooted) return new RegisterOutcome(false);

        // Аптайм считаем от прежнего boot-time до нового: это и есть «сколько продержалась».
        TimeSpan? uptime = prev!.Info.BootTime is { } oldBoot && bootTime is { } newBoot
            ? newBoot - oldBoot
            : null;
        return new RegisterOutcome(true, prev.Info.BootTime, uptime,
            string.IsNullOrWhiteSpace(prev.Info.Activity) ? null : prev.Info.Activity);
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
