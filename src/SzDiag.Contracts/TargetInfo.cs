namespace SzDiag.Contracts;

/// <summary>SSH-цель для диагностики по номеру СЗ.</summary>
public sealed record TargetInfo(string Sz, string Ip, string User, string Ssh);
