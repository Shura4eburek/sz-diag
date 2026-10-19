using SzDiag.HubClient;
using SzDiag.Kb;

namespace SzDiag.Desk.Services;

/// <param name="Ui">Выполнить действие в UI-потоке (FileSystemWatcher зовёт с пула).</param>
public sealed record DeskTools(IHubApiClient Api, ISzcliRunner Szcli, KbPaths Kb, Action<Action> Ui);
