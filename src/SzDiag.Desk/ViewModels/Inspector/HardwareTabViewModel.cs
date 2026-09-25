using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Паспорт железа — `szcli hw passport &lt;сз&gt;` (скрипт живёт в CLI). Снимается при
/// первом открытии вкладки и кнопкой: exec к клиенту, а железо за заявку не меняется само.</summary>
public sealed partial class HardwareTabViewModel(ISzcliRunner szcli) : ObservableObject, IInspectorTab
{
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private string? _sz;

    public string Title => "Железо";
    public TimeSpan? Interval => null;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _busy;

    public async Task RefreshAsync(string sz, CancellationToken ct)
    {
        _sz = sz;
        if (_cache.TryGetValue(sz, out var cached))
        {
            Text = cached;
            return;
        }
        await LoadAsync(sz, ct);
    }

    [RelayCommand]
    private Task Reload()
    {
        if (_sz is not { } sz) return Task.CompletedTask;
        _cache.Remove(sz);
        return LoadAsync(sz, CancellationToken.None);
    }

    private async Task LoadAsync(string sz, CancellationToken ct)
    {
        Busy = true;
        Text = "снимаю паспорт (szcli hw passport)…";
        var r = await szcli.RunAsync(new[] { "hw", "passport", sz }, ct);
        Busy = false;
        if (_sz != sz) return;   // пока снимали, выбрали другую СЗ
        if (r.ExitCode == 0)
        {
            _cache[sz] = r.Output;
            Text = r.Output;
        }
        else
        {
            Text = $"паспорт не снялся (код {r.ExitCode}):\n{r.Output}";
        }
    }

    public void Clear()
    {
        _sz = null;
        Text = "";
    }
}
