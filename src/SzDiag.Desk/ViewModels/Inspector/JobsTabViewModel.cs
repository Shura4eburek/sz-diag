using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.ViewModels.Inspector;

public sealed record JobRow(string Id, string State, string? Script);

/// <summary>Фоновые задачи агента (`szcli exec --jobs`) и хвост вывода выбранной
/// (`exec --result --tail`). Под нагрузкой exec может не ответить — тогда список остаётся
/// прежним, а вкладка говорит об этом прямо.</summary>
public sealed partial class JobsTabViewModel(IHubApiClient api) : ObservableObject, IInspectorTab
{
    public const int OutputTail = 40;

    private string? _sz;
    private bool _reselecting;

    public string Title => "Задачи";
    public TimeSpan? Interval => TimeSpan.FromSeconds(3);

    public ObservableCollection<JobRow> Rows { get; } = new();

    [ObservableProperty] private JobRow? _selected;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _output = "";

    public async Task RefreshAsync(string sz, CancellationToken ct)
    {
        _sz = sz;
        ExecJobStatus? list;
        try
        {
            list = await api.ExecJobsAsync(sz, ct);
        }
        catch (TimeoutException)
        {
            Message = "агент не ответил — под нагрузкой exec глохнет; список на прошлый опрос";
            return;
        }
        catch (HttpRequestException ex)
        {
            Message = $"hub: {ex.Message}";
            return;
        }
        if (list is null)
        {
            Message = "СЗ не на связи";
            return;
        }

        var rows = Parse(list.Tail);
        var keep = Selected?.Id;
        _reselecting = true;
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        Selected = Rows.FirstOrDefault(r => r.Id == keep);
        _reselecting = false;
        Message = rows.Count == 0 ? "фоновых задач нет" : "";
        if (Selected is { } s) await LoadOutputAsync(s, ct);
    }

    partial void OnSelectedChanged(JobRow? value)
    {
        if (_reselecting) return;
        if (value is null) Output = "";
        else _ = LoadOutputAsync(value, CancellationToken.None);
    }

    private async Task LoadOutputAsync(JobRow job, CancellationToken ct)
    {
        if (_sz is not { } sz) return;
        try
        {
            var st = await api.ExecStatusAsync(sz, job.Id, OutputTail, ct);
            if (st is null) return;
            Output = CliXml.Decode(st.Tail) + (st.Error is { } e ? $"\n[ошибка: {e}]" : "");
        }
        catch (TimeoutException)
        {
            // Хвост тот же, что был: под нагрузкой это штатно.
        }
        catch (HttpRequestException ex)
        {
            Output = $"hub: {ex.Message}";
        }
    }

    [GeneratedRegex(@"^(\S+)  (.+)$")]
    private static partial Regex Head();

    /// <summary>Разбор списка агента: строка задачи «id  состояние…», под ней с отступом — начало скрипта.</summary>
    public static IReadOnlyList<JobRow> Parse(string text)
    {
        var rows = new List<JobRow>();
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("    ", StringComparison.Ordinal) && rows.Count > 0)
            {
                rows[^1] = rows[^1] with { Script = line.Trim() };
                continue;
            }
            var m = Head().Match(line);
            if (m.Success) rows.Add(new JobRow(m.Groups[1].Value, m.Groups[2].Value, null));
        }
        return rows;
    }

    public void Clear()
    {
        _sz = null;
        _reselecting = true;
        Rows.Clear();
        Selected = null;
        _reselecting = false;
        Message = "";
        Output = "";
    }
}
