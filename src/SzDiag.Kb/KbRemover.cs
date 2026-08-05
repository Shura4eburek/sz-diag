namespace SzDiag.Kb;

/// <summary>Результат удаления СЗ из vault.</summary>
/// <param name="Existed">Папка СЗ была на месте (иначе удалять нечего).</param>
/// <param name="Path">Путь удалённой папки.</param>
/// <param name="FilesRemoved">Сколько файлов снесено (для отчёта — вдруг там были прогоны).</param>
/// <param name="IncomingLinks">Заметки вне СЗ, которые на неё ссылались: после удаления
/// ссылки станут висячими, и об этом надо сказать вслух, а не оставить на «потом найдётся».</param>
public sealed record KbRemoveResult(
    bool Existed,
    string Path,
    int FilesRemoved,
    IReadOnlyList<string> IncomingLinks);

/// <summary>Удаление СЗ из базы знаний целиком. Появилось после уборки мусорных заявок
/// (`СЗ/--help`, `СЗ/111111`, `СЗ/123123`): чистить приходилось руками через `git rm` +
/// добивание пустых каталогов, которые git не убирает, а Obsidian продолжает показывать
/// в дереве (бэклог п.57).</summary>
public sealed class KbRemover
{
    private readonly KbPaths _paths;
    public KbRemover(KbPaths paths) => _paths = paths;

    /// <summary>Ищет входящие ссылки на СЗ во всём vault, кроме самой папки СЗ.</summary>
    public IReadOnlyList<string> FindIncomingLinks(string sz)
    {
        if (!Directory.Exists(_paths.Root)) return Array.Empty<string>();

        var szDir = Path.GetFullPath(_paths.SzDir(sz));
        var needle = $"[[{sz}";   // ловит и [[160705]], и [[160705|подпись]], и ![[160705]]
        var hits = new List<string>();

        foreach (var file in Directory.EnumerateFiles(_paths.Root, "*.md", SearchOption.AllDirectories))
        {
            if (Path.GetFullPath(file).StartsWith(szDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.ReadAllText(file).Contains(needle, StringComparison.Ordinal))
                hits.Add(file);
        }
        return hits;
    }

    /// <summary>Сносит папку СЗ вместе с вложенными файлами и пустыми каталогами.
    /// Идемпотентно: несуществующая СЗ — не ошибка, а <c>Existed = false</c>.</summary>
    public KbRemoveResult Remove(string sz)
    {
        var dir = _paths.SzDir(sz);
        var links = FindIncomingLinks(sz);
        if (!Directory.Exists(dir)) return new KbRemoveResult(false, dir, 0, links);

        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
        Directory.Delete(dir, recursive: true);
        return new KbRemoveResult(true, dir, files, links);
    }
}
