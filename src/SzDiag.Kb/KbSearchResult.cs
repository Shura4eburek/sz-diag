namespace SzDiag.Kb;

/// <summary>Найденная СЗ с кратким резюме (сырые значения frontmatter).</summary>
public sealed record KbSearchResult(
    string Sz,
    string Order,
    IReadOnlyList<string> Defects,
    IReadOnlyList<string> Replaced);
