namespace SzDiag.Kb;

/// <summary>Входные данные для kb record. Все поля опциональны (merge).</summary>
public sealed class RecordRequest
{
    public required string Sz { get; init; }
    public string? Order { get; init; }
    public string? Device { get; init; }
    public IReadOnlyList<string> Defects { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Replaced { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Symptoms { get; init; } = Array.Empty<string>();
    public string? Status { get; init; }
    public string? Verdict { get; init; }
}
