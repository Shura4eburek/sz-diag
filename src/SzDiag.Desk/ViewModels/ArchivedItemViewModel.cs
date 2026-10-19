using SzDiag.Claude;

namespace SzDiag.Desk.ViewModels;

/// <summary>Сессия СЗ, которой нет в списке hub: закрытая (архив) или заведённая без агента.</summary>
public sealed class ArchivedItemViewModel(SessionRecord r)
{
    public string Key { get; } = r.Key;
    public string Subtitle { get; } = (r.Archived ? "сессия с " : "без агента · с ")
        + $"{r.CreatedAt.ToLocalTime():dd.MM HH:mm}";
}
