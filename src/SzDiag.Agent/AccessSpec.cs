namespace SzDiag.Agent;

/// <summary>Параметры открытия доступа на клиентской машине.</summary>
public sealed record AccessSpec(
    string Sz,
    string ServiceAccount,     // "svc-diag"
    string ServicePublicKey,   // содержимое публичного ключа сервиса
    int SshPort,
    TimeSpan WatchdogTimeout);
