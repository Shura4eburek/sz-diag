namespace SzDiag.Contracts;

/// <summary>Снимок активной сессии СЗ для реестра и CLI.</summary>
/// <param name="BootTime">Время загрузки ОС клиента (null у агентов старых сборок).</param>
/// <param name="LastRebootAt">Когда hub зафиксировал смену boot-time, т.е. реальный ребут
/// клиента. Отличает «машина перезагрузилась» от «heartbeat опоздал под нагрузкой».</param>
public sealed record SessionInfo(
    string Sz,
    string Ip,
    string Hostname,
    SessionStatus Status,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastHeartbeat,
    string Activity = "",
    DateTimeOffset? ActivitySince = null,
    DateTimeOffset? BootTime = null,
    DateTimeOffset? LastRebootAt = null,
    /// <summary>Сколько вырубонов hub насчитал за эту сессию (с момента своего старта).
    /// Полная история переживает рестарт hub и лежит в SQLite — `szcli reboots &lt;СЗ&gt;`.</summary>
    int RebootCount = 0,
    /// <summary>Заполнено, когда `agent.exe --revert` (watchdog/headless, без SignalR) не
    /// откатился чисто — доступ на клиенте мог остаться навсегда. `list`/`watch` обязаны
    /// показать СЗ проблемной, а не online (бэклог п.59, СЗ 160705).</summary>
    string? RevertNote = null,
    /// <summary>Под кем работает агент (см. <see cref="RegisterRequest.AgentUser"/>) — null у
    /// агентов старых сборок.</summary>
    string? AgentUser = null,
    /// <summary>Сессия Windows агента — 0 значит «GUI недоступен» (бэклог п.220).</summary>
    int? AgentSessionId = null,
    /// <summary>Адрес машины в её собственной сети (со слов агента) — справочный. Адресом
    /// подключения не является: за туннелем и за NAT по нему не достучаться.</summary>
    string? LanIp = null,
    /// <summary>Имя, по которому хост подключается к клиенту (quick tunnel). null — туннеля
    /// нет, адресом остаётся <see cref="Ip"/>.</summary>
    string? AccessHost = null,
    /// <summary>См. <see cref="SzDiag.Contracts.AccessMode"/>. null у агентов старых сборок —
    /// считать Direct.</summary>
    string? AccessMode = null,
    /// <summary>Публичный host-ключ sshd клиента для пиннинга в known_hosts.</summary>
    string? SshHostKeyFingerprint = null)
{
    /// <summary>Агент сидит в служебной сессии без рабочего стола — GUI-операции (Start-Process
    /// без задачи в интерактивной сессии, скриншот) там ломаются молча (бэклог п.220).</summary>
    public bool AgentInSessionZero => AgentSessionId == 0;
}
