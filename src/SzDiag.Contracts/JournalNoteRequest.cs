namespace SzDiag.Contracts;

/// <summary>Ручная запись в журнал СЗ (`szcli note`): то, что софтом не видно —
/// свап БП, правка BIOS, осмотр мастером.</summary>
public sealed record JournalNoteRequest(string Text);
