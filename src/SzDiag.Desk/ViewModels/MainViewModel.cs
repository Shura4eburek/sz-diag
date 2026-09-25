using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SzDiag.Claude;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels.Inspector;
using SzDiag.HubClient;

namespace SzDiag.Desk.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeskUiState _ui;
    private readonly TimeProvider _time;
    private readonly ChatServices? _chat;
    private readonly FreezeProbe? _freeze;
    private readonly Dictionary<string, ChatViewModel> _chats = new(StringComparer.Ordinal);

    /// <summary>Сколько СЗ должна отсутствовать в списке hub, чтобы её сессия ушла в архив:
    /// после рестарта hub список пуст, пока агенты не переподключатся (ревью I-2).</summary>
    public static readonly TimeSpan ArchiveAfter = TimeSpan.FromMinutes(3);

    /// <summary>С какого момента СЗ, бывшая в списке, в нём отсутствует.</summary>
    private readonly Dictionary<string, DateTimeOffset> _missingSince = new(StringComparer.Ordinal);

    public MainViewModel(HubPoller poller, DeskUiState ui, TimeProvider time, ChatServices? chat = null,
        InspectorViewModel? inspector = null, FreezeProbe? freeze = null)
    {
        Inspector = inspector;
        _freeze = freeze;
        Poller = poller;
        _ui = ui;
        _time = time;
        _chat = chat;
        _isInspectorOpen = ui.InspectorOpen;
        if (chat is null) return;
        chat.Tokens.Changed += () => chat.Ui(UpdateTokens);
        chat.Broker.Requested += _ => chat.Ui(() => AttentionNeeded?.Invoke());
        UpdateTokens();
    }

    public HubPoller Poller { get; }

    /// <summary>Правая панель: вкладки выбранной СЗ; null — окно без инспектора (тесты части 1).</summary>
    public InspectorViewModel? Inspector { get; }
    public ObservableCollection<SzItemViewModel> Items { get; } = new();
    public ObservableCollection<ArchivedItemViewModel> Archived { get; } = new();
    public ObservableCollection<TransferItemViewModel> Transfers { get; } = new();
    public StatusBarViewModel Status { get; } = new();

    /// <summary>Пришёл запрос разрешения — окно мигает в панели задач.</summary>
    public event Action? AttentionNeeded;

    public bool HasPendingPermissions => _chat?.Broker.PendingCount > 0;

    [ObservableProperty] private SzItemViewModel? _selected;
    [ObservableProperty] private ArchivedItemViewModel? _selectedArchived;
    [ObservableProperty] private ChatViewModel? _activeChat;
    [ObservableProperty] private bool _hasTransfers;
    [ObservableProperty] private bool _hasArchived;
    [ObservableProperty] private bool _isInspectorOpen;

    public bool CanStartSession => _chat is not null && Selected is not null && ActiveChat is null;
    public bool ShowPlaceholder => Selected is null && ActiveChat is null;
    public string Title => ActiveChat?.Key ?? Selected?.Sz ?? "SzDiag";

    partial void OnIsInspectorOpenChanged(bool value) => _ui.InspectorOpen = value;

    partial void OnSelectedChanged(SzItemViewModel? value)
    {
        Inspector?.Select(value?.Sz);
        if (value is not null) SelectedArchived = null;
        ActiveChat = value is not null ? ChatFor(value.Sz)
            : SelectedArchived is not null ? ChatFor(SelectedArchived.Key)
            : null;
        NotifyCenter();
    }

    partial void OnSelectedArchivedChanged(ArchivedItemViewModel? value)
    {
        if (value is null)
        {
            if (Selected is null) ActiveChat = null;
            return;
        }
        Selected = null;
        ActiveChat = ChatFor(value.Key);
    }

    partial void OnActiveChatChanged(ChatViewModel? value) => NotifyCenter();

    private void NotifyCenter()
    {
        OnPropertyChanged(nameof(CanStartSession));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Найденные профили Claude: на каждый — своя кнопка «Начать сессию».</summary>
    public IReadOnlyList<string> Profiles => _chat?.Profiles ?? Array.Empty<string>();

    /// <summary>«Начать сессию» — только руками: автозапуска нет (токены, спека). Профиль
    /// записывается явно даже без выбора: иначе «по умолчанию» после появления нового профиля
    /// сменилось бы, и --resume искал бы разговор не в том каталоге.</summary>
    [RelayCommand]
    private void StartSession(string? profile)
    {
        if (_chat is null || Selected is null) return;
        _chat.Sessions.Create(Selected.Sz, profile ?? _chat.Profiles.FirstOrDefault());
        ActiveChat = ChatFor(Selected.Sz);
        RefreshSessionBadges();
    }

    private ChatViewModel? ChatFor(string key)
    {
        if (_chat is null) return null;
        if (_chats.TryGetValue(key, out var vm)) return vm;
        var session = _chat.Sessions.Get(key);
        if (session is null) return null;
        vm = new ChatViewModel(session, _chat.Broker, _chat.Terminal, _chat.Ui);
        session.Changed += () => _chat.Ui(RefreshSessionBadges);
        _chats[key] = vm;
        return vm;
    }

    /// <summary>Вызывать в UI-потоке (окно маршалит событие <see cref="HubPoller.Changed"/>).</summary>
    public void Apply(HubSnapshot s)
    {
        var now = _time.GetUtcNow();
        var selectedSz = Selected?.Sz;
        var before = Items.ToDictionary(i => i.Sz, i => (i.Liveness, i.RebootCount), StringComparer.Ordinal);
        CollectionSync.Sync(Items, s.Sessions.OrderBy(x => x.Sz, StringComparer.Ordinal),
            x => x.Sz, vm => vm.Sz, x => new SzItemViewModel(x, now), (vm, x) => vm.Update(x, now));
        if (selectedSz is not null && Items.All(i => i.Sz != selectedSz)) Selected = null;
        if (_freeze is not null)
            foreach (var item in Items) item.IsFrozen = _freeze.IsFrozen(item.Sz);
        // ⚡N выбранной СЗ вырос — вкладка вырубонов перечитывается сама.
        if (Selected is { } sel && before.TryGetValue(sel.Sz, out var was) && sel.RebootCount > was.RebootCount)
            Inspector?.OnRebootCountChanged();

        CollectionSync.Sync(Transfers, s.Transfers, t => t.Id, vm => vm.Id,
            t => new TransferItemViewModel(t), (vm, t) => vm.Update(t));
        HasTransfers = Transfers.Count > 0;

        Status.Apply(s, now);

        // Без удачного опроса списка СЗ пустой список значит «ещё не знаю», а не «все СЗ закрыты»:
        // иначе первый же опрос передач отправил бы все сессии в архив.
        if (_chat is null || s.SessionsOkAt is null || s.IsStale) return;
        NoteMachineChanges(before);
        ArchiveClosed(s, before.Keys, now);
        RefreshArchived();
        RefreshSessionBadges();
    }

    /// <summary>События машины — серыми строками в ленту её сессии. Уверенного «вырубон» по
    /// молчанию heartbeat не пишем: подтверждает отказ только смена boot (CLAUDE.md, п.42).</summary>
    private void NoteMachineChanges(Dictionary<string, (SzLivenessState Liveness, int RebootCount)> before)
    {
        foreach (var item in Items)
        {
            if (!before.TryGetValue(item.Sz, out var was)) continue;
            if (_chat!.Sessions.Get(item.Sz) is not { } session) continue;
            var online = item.Liveness == SzLivenessState.Online;
            var wasOnline = was.Liveness == SzLivenessState.Online;
            var rebooted = item.RebootCount > was.RebootCount;
            if (wasOnline && !online)
                session.Note("связь с машиной пропала: heartbeat молчит (под нагрузкой это бывает лагом — вырубон подтвердит только смена boot)");
            else if (!wasOnline && online)
                session.Note(rebooted
                    ? $"машина вернулась, boot сменился — был ребут или вырубон (⚡{item.RebootCount})"
                    : "машина снова на связи, boot прежний — это был лаг");
            else if (rebooted)
                session.Note($"boot сменился — был ребут или вырубон (⚡{item.RebootCount})");
        }
    }

    /// <summary>В архив — только СЗ, которая была в списке и пропала (закрыли), и только если её
    /// нет дольше <see cref="ArchiveAfter"/>: рестарт hub на минуту опустошает список целиком
    /// (ревью I-2). Идущий ход не рубим — архив ждёт его конца. Не «любая сессия без СЗ в списке»:
    /// иначе продолженный из архива разговор архивировался бы следующим же опросом.</summary>
    private void ArchiveClosed(HubSnapshot s, IEnumerable<string> wasLive, DateTimeOffset now)
    {
        var live = s.Sessions.Select(x => x.Sz).ToHashSet(StringComparer.Ordinal);
        foreach (var key in wasLive)
            if (!live.Contains(key)) _missingSince.TryAdd(key, now);
        foreach (var key in _missingSince.Keys.ToList())
        {
            if (live.Contains(key))
            {
                _missingSince.Remove(key);
                continue;
            }
            if (now - _missingSince[key] < ArchiveAfter) continue;
            if (_chat!.Sessions.Get(key) is not { } session)
            {
                _missingSince.Remove(key);
                continue;
            }
            if (session.State is SessionState.Working or SessionState.WaitingPermission) continue;
            _missingSince.Remove(key);
            _ = session.ArchiveAsync();
        }
    }

    private void RefreshArchived()
    {
        var live = Items.Select(i => i.Sz).ToHashSet(StringComparer.Ordinal);
        CollectionSync.Sync(Archived,
            _chat!.Sessions.Records.Where(r => !live.Contains(r.Key)).OrderByDescending(r => r.CreatedAt),
            r => r.Key, vm => vm.Key, r => new ArchivedItemViewModel(r), (_, _) => { });
        if (SelectedArchived is not null && !Archived.Contains(SelectedArchived)) SelectedArchived = null;
        HasArchived = Archived.Count > 0;
    }

    private void RefreshSessionBadges()
    {
        if (_chat is null) return;
        foreach (var item in Items) item.SessionState = _chat.Sessions.Peek(item.Sz)?.State;
    }

    private void UpdateTokens()
        => Status.TokensText = StatusBarViewModel.FormatTokens(_chat!.Tokens.Today, _chat.Tokens.CostToday);
}
