using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Правая панель: вкладки выбранной СЗ. Обновляет только видимую — по её интервалу,
/// раз в секунду из окна (<see cref="RunLoopAsync"/>). Индекс 0 — «Обзор» (null: данные в
/// карточке СЗ, опрашивать нечего).</summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    private readonly IReadOnlyList<IInspectorTab?> _tabs;
    private readonly TimeProvider _time;
    private readonly Dictionary<int, DateTimeOffset> _refreshedAt = new();
    private readonly HashSet<int> _inFlight = new();
    private CancellationTokenSource _szCts = new();

    public InspectorViewModel(IReadOnlyList<IInspectorTab?> tabs, TimeProvider time)
    {
        _tabs = tabs;
        _time = time;
    }

    public string? Sz { get; private set; }

    [ObservableProperty] private int _selectedIndex;

    public IInspectorTab? Current => SelectedIndex >= 0 && SelectedIndex < _tabs.Count ? _tabs[SelectedIndex] : null;

    partial void OnSelectedIndexChanged(int value) => _ = RefreshIfDueAsync();

    public void Select(string? sz)
    {
        if (sz == Sz) return;
        Sz = sz;
        _szCts.Cancel();
        _szCts = new CancellationTokenSource();
        _refreshedAt.Clear();
        foreach (var tab in _tabs) tab?.Clear();
        _ = RefreshIfDueAsync();
    }

    public void OnRebootCountChanged()
    {
        for (var i = 0; i < _tabs.Count; i++)
            if (_tabs[i] is { RefreshOnReboot: true }) _refreshedAt.Remove(i);
        _ = RefreshIfDueAsync();
    }

    public Task TickAsync() => RefreshIfDueAsync();

    public async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
            await TickAsync();
        }
    }

    private async Task RefreshIfDueAsync()
    {
        var index = SelectedIndex;
        var tab = Current;
        var sz = Sz;
        if (tab is null || sz is null || _inFlight.Contains(index)) return;

        var now = _time.GetUtcNow();
        if (_refreshedAt.TryGetValue(index, out var at) && (tab.Interval is not { } every || now - at < every)) return;

        _inFlight.Add(index);
        _refreshedAt[index] = now;
        try
        {
            await tab.RefreshAsync(sz, _szCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Выбрали другую СЗ посреди обновления.
        }
        catch (Exception ex)
        {
            DeskLog.Write($"инспектор, вкладка «{tab.Title}»: {ex.Message}");
        }
        finally
        {
            _inFlight.Remove(index);
        }
    }
}
