namespace SzDiag.Contracts;

/// <summary>Имена, общие для агента и hub, чтобы не расходились строки.</summary>
public static class HubRoutes
{
    public const string Path = "/agents";

    // Заголовок с pre-shared токеном при коннекте.
    public const string TokenHeader = "X-SzDiag-Token";

    // Методы, которые агент вызывает на hub.
    public const string Register = nameof(Register);
    public const string Heartbeat = nameof(Heartbeat);

    // Метод, который hub вызывает на агенте (client method).
    public const string Revert = nameof(Revert);
}
