using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SzDiag.Claude;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels;

public sealed partial class ChatViewModel : ObservableObject
{
    private readonly ClaudeSession _session;
    private readonly ITerminalLauncher _terminal;
    private readonly Action<Action> _ui;

    public ChatViewModel(ClaudeSession session, PermissionBroker broker, ITerminalLauncher terminal, Action<Action> ui)
    {
        _session = session;
        _terminal = terminal;
        _ui = ui;
        Feed = new FeedBuilder((id, allow) => broker.Resolve(id, allow), session.Restart);
        foreach (var e in session.Attach(ev => _ui(() => Feed.Add(ev)))) Feed.Add(e);
        Feed.ExpirePermissionsExcept(broker.Pending(session.Key).Select(p => p.RequestId));
        session.Changed += () => _ui(Refresh);
        Refresh();
    }

    public string Key => _session.Key;

    /// <summary>Профиль Claude, в котором идёт разговор (показывается в шапке чата).</summary>
    public string? Profile => _session.Profile;
    public FeedBuilder Feed { get; }
    public ObservableCollection<FeedItemViewModel> Items => Feed.Items;

    [ObservableProperty] private string _draft = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStop), nameof(StateText))] private SessionState _state;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasQueue))] private int _queued;

    public bool CanStop => State is SessionState.Working or SessionState.WaitingPermission or SessionState.AnsweringPeer;
    public bool HasQueue => Queued > 0;

    public string StateText => State switch
    {
        SessionState.Stopped when _session.SessionId is null => "новая сессия — напиши первое сообщение",
        SessionState.Stopped => "остановлена — сообщение продолжит её (--resume)",
        SessionState.Idle => "готова",
        SessionState.Working => "работает…",
        SessionState.WaitingPermission => "ждёт разрешения",
        SessionState.AnsweringPeer => "отвечает на вопрос соседней сессии…",
        SessionState.Crashed => "упала — см. карточку в ленте",
        _ => "архив — сообщение продолжит сессию",
    };

    private void Refresh()
    {
        State = _session.State;
        Queued = _session.Queued.Count;
        OnPropertyChanged(nameof(StateText));
    }

    [RelayCommand]
    private async Task Send()
    {
        var text = Draft.Trim();
        if (text.Length == 0) return;
        Draft = "";
        await _session.SendAsync(text);
    }

    [RelayCommand]
    private async Task Stop()
    {
        var back = await _session.InterruptAsync();
        if (back.Count == 0) return;
        // Неотправленное возвращается в поле ввода, как Esc в терминальном Claude Code:
        // «■» — это «стоп», а не «выброси всё, что я успел написать».
        Draft = string.Join("\n\n", back.Append(Draft).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    [RelayCommand]
    private void Restart() => _session.Restart();

    [RelayCommand]
    private async Task OpenInTerminal()
    {
        if (_session.SessionId is not { } id)
        {
            _session.Note("в терминале открывать нечего: сессия ещё не началась");
            return;
        }
        await _session.DetachAsync();
        if (!_terminal.Open(_session.Key, id))
            _session.Note("терминал не открылся: claude.exe не найден или нет ни wt.exe, ни cmd.exe");
    }
}
