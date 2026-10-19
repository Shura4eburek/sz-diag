using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <param name="Key">Пусто — строка без «ключ : значение» (например, «событий TDR нет»).</param>
public sealed record HwRow(string Key, string Value)
{
    public bool HasKey => Key.Length > 0;
}

public sealed record HwSection(string Title, IReadOnlyList<HwRow> Rows)
{
    public bool HasTitle => Title.Length > 0;
}

public sealed record HwPassport(IReadOnlyList<HwSection> Sections, string? Stderr);

/// <summary>Паспорт железа — `szcli hw passport &lt;сз&gt;` (скрипт живёт в CLI). Снимается при
/// первом открытии вкладки и кнопкой: exec к клиенту, а железо за заявку не меняется само.
/// Вывод разбирается на секции и строки «ключ — значение»: сырой текст в 290 px переносился
/// посреди выравнивания и не читался (160176).</summary>
public sealed partial class HardwareTabViewModel(ISzcliRunner szcli) : ObservableObject, IInspectorTab
{
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private string? _sz;

    public string Title => "Железо";
    public TimeSpan? Interval => null;

    /// <summary>Сообщение вместо паспорта: «снимаю…», ошибка. Пусто — показан паспорт.</summary>
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private IReadOnlyList<HwSection> _sections = Array.Empty<HwSection>();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStderr))] private string? _stderr;

    public bool HasStderr => !string.IsNullOrEmpty(Stderr);

    [GeneratedRegex(@"^===\s*(.*?)\s*===$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\S.*?)\s+:\s?(.*)$")]
    private static partial Regex KeyValue();

    public static HwPassport Parse(string output)
    {
        var sections = new List<HwSection>();
        var title = "";
        var rows = new List<HwRow>();
        string? stderr = null;
        void Flush()
        {
            if (rows.Count > 0 || title.Length > 0) sections.Add(new HwSection(title, rows.ToList()));
            rows.Clear();
        }
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("stderr:", StringComparison.Ordinal))
            {
                stderr = line["stderr:".Length..].Trim();
                continue;
            }
            if (Heading().Match(line) is { Success: true } h)
            {
                Flush();
                title = h.Groups[1].Value;
                continue;
            }
            rows.Add(KeyValue().Match(line) is { Success: true } kv
                ? new HwRow(kv.Groups[1].Value.Trim(), kv.Groups[2].Value.Trim())
                : new HwRow("", line.Trim()));
        }
        Flush();
        return new HwPassport(sections, stderr);
    }

    public async Task RefreshAsync(string sz, CancellationToken ct)
    {
        _sz = sz;
        if (_cache.TryGetValue(sz, out var cached))
        {
            Show(cached);
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
        Sections = Array.Empty<HwSection>();
        Stderr = null;
        Text = "снимаю паспорт (szcli hw passport)…";
        var r = await szcli.RunAsync(new[] { "hw", "passport", sz }, ct);
        Busy = false;
        if (_sz != sz) return;   // пока снимали, выбрали другую СЗ
        if (r.ExitCode == 0)
        {
            _cache[sz] = r.Output;
            Show(r.Output);
        }
        else
        {
            Text = $"паспорт не снялся (код {r.ExitCode}):\n{r.Output}";
        }
    }

    /// <summary>Разобранный паспорт; не разобралось ни одной строки — сырой текст как есть.</summary>
    private void Show(string output)
    {
        var p = Parse(output);
        Sections = p.Sections;
        Stderr = p.Stderr;
        Text = p.Sections.Count == 0 ? output : "";
    }

    public void Clear()
    {
        _sz = null;
        Text = "";
        Sections = Array.Empty<HwSection>();
        Stderr = null;
    }
}
