namespace SzDiag.Contracts;

/// <summary>Payload регистрации агента. IP берётся из соединения, не из payload.</summary>
public sealed record RegisterRequest(string Sz, string Hostname);
