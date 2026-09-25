using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeskUiState _ui;
    private readonly TimeProvider _time;

    public MainViewModel(HubPoller poller, DeskUiState ui, TimeProvider time)
    {
        Poller = poller;
        _ui = ui;
        _time = time;
        _isInspectorOpen = ui.InspectorOpen;
    }

    public HubPoller Poller { get; }
    public ObservableCollection<SzItemViewModel> Items { get; } = new();
    public ObservableCollection<TransferItemViewModel> Transfers { get; } = new();
    public StatusBarViewModel Status { get; } = new();

    [ObservableProperty] private SzItemViewModel? _selected;
    [ObservableProperty] private bool _hasTransfers;
    [ObservableProperty] private bool _isInspectorOpen;

    partial void OnIsInspectorOpenChanged(bool value) => _ui.InspectorOpen = value;

    /// <summary>Вызывать в UI-потоке (окно маршалит событие <see cref="HubPoller.Changed"/>).</summary>
    public void Apply(HubSnapshot s)
    {
        var now = _time.GetUtcNow();
        var selectedSz = Selected?.Sz;
        CollectionSync.Sync(Items, s.Sessions.OrderBy(x => x.Sz, StringComparer.Ordinal),
            x => x.Sz, vm => vm.Sz, x => new SzItemViewModel(x, now), (vm, x) => vm.Update(x, now));
        if (selectedSz is not null && Items.All(i => i.Sz != selectedSz)) Selected = null;

        CollectionSync.Sync(Transfers, s.Transfers, t => t.Id, vm => vm.Id,
            t => new TransferItemViewModel(t), (vm, t) => vm.Update(t));
        HasTransfers = Transfers.Count > 0;

        Status.Apply(s, now);
    }
}
