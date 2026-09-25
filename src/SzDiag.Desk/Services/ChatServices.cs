using SzDiag.Claude;

namespace SzDiag.Desk.Services;

/// <param name="Profiles">Имена найденных профилей Claude; первый — по умолчанию.</param>
/// <param name="Ui">Выполнить действие в UI-потоке (события сессий приходят с потоков процессов).</param>
public sealed record ChatServices(SessionManager Sessions, PermissionBroker Broker, TokenLedger Tokens,
    ITerminalLauncher Terminal, IReadOnlyList<string> Profiles, Action<Action> Ui);
