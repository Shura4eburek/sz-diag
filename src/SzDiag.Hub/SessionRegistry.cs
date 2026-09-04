using System.Collections.Concurrent;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Потокобезопасный реестр активных сессий СЗ. Один экземпляр (singleton).</summary>
public sealed class SessionRegistry
{
    private sealed record Entry(SessionInfo Info, string ConnectionId);

    /// <summary>Насколько boot-time может «дрожать» между регистрациями, оставаясь тем же.
    /// Агент считает его от собственных часов как «сейчас минус аптайм» (так кривая таймзона
    /// WinPE перестаёт всё ломать — бэклог п.90), и два запуска подряд дают значения,
    /// расходящиеся на секунды. Ребутом считаем только заметный сдвиг.</summary>
    public static readonly TimeSpan RebootTolerance = TimeSpan.FromMinutes(2);

    /// <summary>Boot-time из будущего недостоверен (часы клиента, таймзона PE): по нему нельзя
    /// ни считать аптайм, ни заводить вырубоны.</summary>
    public static readonly TimeSpan FutureBootSlack = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _bySz = new();
    private readonly TimeProvider _time;
    private DateTimeOffset? _lastMassOfflineAt;

    public SessionRegistry(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Признак 2 планового обесточивания (бэклог п.130): пропажа heartbeat сразу у
    /// нескольких СЗ — это свет, а не дефект одной машины. <see cref="OfflineSweeper"/> вызывает
    /// это, когда в одном цикле offline ушло сразу несколько сессий.</summary>
    public void RecordMassOfflineEvent(DateTimeOffset? at = null) => _lastMassOfflineAt = at ?? _time.GetUtcNow();

    /// <summary>Была ли недавняя массовая пропажа heartbeat в пределах <paramref name="window"/>
    /// от момента <paramref name="at"/> — используется при классификации конкретного ребута.</summary>
    public bool WasMassOfflineNear(DateTimeOffset at, TimeSpan window)
        => _lastMassOfflineAt is { } t && (at - t).Duration() <= window;

    /// <summary>Что произошло при регистрации агента.</summary>
    /// <param name="Rebooted">Boot-time сменился — машина реально перезагрузилась.</param>
    /// <param name="PreviousBootTime">Прежний boot-time (для записи события).</param>
    /// <param name="UptimeBefore">Сколько машина продержалась до вырубона.</param>
    /// <param name="ActivityBefore">Чем была занята — «продержалась N минут под тестом».</param>
    /// <param name="ReconnectedAfterGap">Заполнено, когда boot-time НЕ сменился, но прошлая
    /// сессия была помечена offline (heartbeat пропадал) — сколько молчала связь. Отвал под
    /// нагрузкой без реального ребута иначе не оставляет в журнале ни следа (бэклог п.202,
    /// СЗ 161972: «вырубился или висит» пришлось выяснять руками).</param>
    public sealed record RegisterOutcome(
        bool Rebooted,
        DateTimeOffset? PreviousBootTime = null,
        TimeSpan? UptimeBefore = null,
        string? ActivityBefore = null,
        TimeSpan? ReconnectedAfterGap = null);

    /// <summary>Регистрация (или переподключение) агента. Если у СЗ уже был известен boot-time
    /// и пришёл другой — значит клиент реально перезагрузился: фиксируем момент в
    /// <see cref="SessionInfo.LastRebootAt"/>, считаем ребуты и отдаём подробности вызывающему.
    /// Это единственный надёжный признак ребута: пропажа heartbeat под нагрузкой ребутом не
    /// является, а ICMP у типовой клиентской винды закрыт из коробки.</summary>
    /// <param name="lastShutdown">Чем закончилась прошлая сессия ОС по журналу клиента
    /// (<see cref="ShutdownKind"/>). Выключение кнопкой в счётчик отказов не идёт: на 161312
    /// два «аварийных выключения» из пяти были нажатием кнопки (бэклог п.93).</param>
    public RegisterOutcome Register(string sz, string ip, string hostname, string connectionId,
        DateTimeOffset? bootTime = null, string? lastShutdown = null)
    {
        var now = _time.GetUtcNow();
        _bySz.TryGetValue(sz, out var prev);

        // Boot-time из будущего (часы клиента, дефолтная таймзона WinPE) не годится ни для
        // аптайма, ни для детекта вырубона — считаем, что его нет вовсе (бэклог п.90).
        if (bootTime is { } future && future - now > FutureBootSlack) bootTime = null;

        var rebooted = prev is not null
                       && prev.Info.BootTime is { } was
                       && bootTime is { } isNow
                       && (isNow - was).Duration() > RebootTolerance;

        var lastReboot = rebooted ? now : prev?.Info.LastRebootAt;
        var countsAsFailure = rebooted && ShutdownKind.CountsAsFailure(lastShutdown);
        var rebootCount = (prev?.Info.RebootCount ?? 0) + (countsAsFailure ? 1 : 0);
        var info = new SessionInfo(sz, ip, hostname, SessionStatus.Online, now, now,
            BootTime: bootTime, LastRebootAt: lastReboot, RebootCount: rebootCount);
        _bySz[sz] = new Entry(info, connectionId);

        if (!rebooted)
        {
            // Переподключение после пропажи heartbeat, но БЕЗ смены boot-time: машина не
            // ребутилась, просто молчала — тоже факт диагностики (бэклог п.202).
            var gap = prev is { Info.Status: SessionStatus.Offline }
                ? now - prev.Info.LastHeartbeat
                : (TimeSpan?)null;
            var busyDuringGap = gap is not null && !string.IsNullOrWhiteSpace(prev!.Info.Activity)
                ? prev.Info.Activity
                : null;
            return new RegisterOutcome(false, ActivityBefore: busyDuringGap, ReconnectedAfterGap: gap);
        }

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

    /// <summary>Итог `agent.exe --revert`, пришедший по HTTP (watchdog/headless-откат — там
    /// нет живого SignalR-коннекта для обычного ответа). Успех — сессия закрыта штатно,
    /// убираем её из реестра. Неудача — доступ на клиенте мог остаться навсегда (бэклог п.59,
    /// СЗ 160705: watchdog упал на середине, а hub так и показывал СЗ online), поэтому метим
    /// проблемной вместо online: `false` — записывать некуда, СЗ уже не в реестре.</summary>
    public bool MarkRevertOutcome(string sz, bool success, string note)
    {
        if (success) { Remove(sz); return true; }
        if (!_bySz.TryGetValue(sz, out var e)) return false;
        _bySz[sz] = e with { Info = e.Info with { Status = SessionStatus.Offline, RevertNote = note } };
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

    /// <summary>Снимок сессии — нужен, например, чтобы решить, поднять ли дефолтный таймаут
    /// `exec`, когда на СЗ прямо сейчас идёт стресс-прогон (бэклог п.35a).</summary>
    public SessionInfo? TryGetInfo(string sz)
        => _bySz.TryGetValue(sz, out var e) ? e.Info : null;

    public IReadOnlyList<SessionInfo> GetActive()
        => _bySz.Values.Select(e => e.Info).ToList();
}
