using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.ViewModels.Inspector;

public sealed record RebootRow(string When, string Kind, string Detail, bool IsFailure);

/// <summary>Таймлайн вырубонов (то же, что `szcli reboots`): когда, чем был (обрыв/кнопка/BSOD —
/// по журналу клиента), сколько продержалась и чем была занята.</summary>
public sealed partial class RebootsTabViewModel(IHubApiClient api) : ObservableObject, IInspectorTab
{
    public string Title => "Вырубоны";
    public TimeSpan? Interval => TimeSpan.FromSeconds(30);   // hub шлёт ⚡ только на провалы, штатный ребут — нет
    public bool RefreshOnReboot => true;

    public ObservableCollection<RebootRow> Rows { get; } = new();

    [ObservableProperty] private string _summary = "";

    public async Task RefreshAsync(string sz, CancellationToken ct)
    {
        RebootTimeline? t;
        try
        {
            t = await api.GetRebootsAsync(sz, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Прежний список не стираем: он верен на момент прошлого ответа.
            Summary = $"hub не отдал таймлайн: {ex.Message}";
            return;
        }

        Rows.Clear();
        var watching = t?.WatchingSince is { } since ? $" (наблюдение с {since.ToLocalTime():dd.MM HH:mm})" : "";
        if (t is null || t.Events.Count == 0)
        {
            Summary = "вырубонов не зафиксировано" + watching;
            return;
        }
        foreach (var e in t.Events.OrderByDescending(e => e.At)) Rows.Add(Row(e));
        var longest = t.MaxUptimeSeconds is { } m ? $" · дольше всего без ребута: {Span(TimeSpan.FromSeconds(m))}" : "";
        Summary = $"событий: {t.Events.Count}, отказов: {t.Events.Count(e => e.IsFailure)}{longest}{watching}";
    }

    internal static RebootRow Row(RebootEvent e)
    {
        var kind = ShutdownKind.Describe(e.Kind);
        if (e.Bugcheck is { } code and not 0) kind += " " + BugcheckCodes.Format((uint)code);
        var parts = new List<string>();
        if (e.UptimeBefore is { } up) parts.Add($"продержалась {Span(up)}");
        if (!string.IsNullOrEmpty(e.ActivityBefore)) parts.Add($"занята: {e.ActivityBefore}");
        if (e.Source == RebootSource.Journal) parts.Add("из журнала клиента");
        return new RebootRow($"{e.At.ToLocalTime():dd.MM HH:mm}", kind, string.Join(" · ", parts), e.IsFailure);
    }

    internal static string Span(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}ч {t.Minutes:00}м" : $"{(int)t.TotalMinutes}м";

    public void Clear()
    {
        Rows.Clear();
        Summary = "";
    }
}
