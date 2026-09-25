using SzDiag.Claude;

namespace SzDiag.Desk.ViewModels;

/// <summary>Сессия закрытой СЗ в секции «АРХИВ»: СЗ из списка hub пропала, разговор остался.</summary>
public sealed class ArchivedItemViewModel(SessionRecord r)
{
    public string Key { get; } = r.Key;
    public string Subtitle { get; } = $"сессия с {r.CreatedAt.ToLocalTime():dd.MM HH:mm}";
}
