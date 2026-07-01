namespace SzDiag.Contracts;

/// <summary>Запись для персистенса и истории открытий/закрытий.</summary>
public sealed record SessionRecord(
    string Sz,
    string Ip,
    string Hostname,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt);
