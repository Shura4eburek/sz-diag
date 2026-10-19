using System.Text.Json;

namespace SzDiag.Desk.Services;

/// <summary>Мелкие настройки окна между запусками (открыт ли инспектор). Битый/недоступный
/// файл — не повод не открыть окно: берём значения по умолчанию.</summary>
public sealed class DeskUiState
{
    public bool InspectorOpen { get; set; } = true;

    public static DeskUiState Load(string path)
    {
        try { return JsonSerializer.Deserialize<DeskUiState>(File.ReadAllText(path)) ?? new(); }
        catch { return new(); }
    }

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this)); }
        catch { /* рядом с exe писать нельзя — просто не запомним */ }
    }
}
