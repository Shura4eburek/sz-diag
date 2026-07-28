namespace SzDiag.Contracts;

/// <summary>Payload регистрации агента. IP берётся из соединения, не из payload.
/// <paramref name="BootTime"/> — время загрузки ОС клиента: hub по нему отличает реальный
/// ребут (boot-time сменился) от лага heartbeat под нагрузкой (boot-time тот же). Nullable,
/// т.к. агенты старых сборок поле не шлют; читается один раз при старте агента и не меняется,
/// пока машина не перезагрузилась — поэтому в heartbeat его гонять не нужно.</summary>
public sealed record RegisterRequest(string Sz, string Hostname, DateTimeOffset? BootTime = null);
