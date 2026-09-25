using SzDiag.Claude;

namespace SzDiag.Desk.Services;

/// <param name="Ui">Выполнить действие в UI-потоке (события сессий приходят с потоков процессов).</param>
public sealed record ChatServices(SessionManager Sessions, PermissionBroker Broker, TokenLedger Tokens,
    ITerminalLauncher Terminal, Action<Action> Ui);
