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
    int RebootCount = 0);
