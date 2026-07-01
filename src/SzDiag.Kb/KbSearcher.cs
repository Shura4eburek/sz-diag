namespace SzDiag.Kb;

/// <summary>Поиск по прошлым СЗ: по номеру заказа (frontmatter) и/или свободному тексту.</summary>
public sealed class KbSearcher
{
    private readonly KbPaths _paths;
    public KbSearcher(KbPaths paths) => _paths = paths;

    public IReadOnlyList<KbSearchResult> Search(string? order, string? text)
    {
        var results = new List<KbSearchResult>();
        if (!Directory.Exists(_paths.SzRoot)) return results;

        foreach (var dir in Directory.GetDirectories(_paths.SzRoot))
        {
            var sz = Path.GetFileName(dir);
            var homePath = _paths.HomeNote(sz);
            if (!File.Exists(homePath)) continue;

            var fm = FrontmatterEditor.Load(File.ReadAllText(homePath));

            if (order is not null)
            {
                var orderRaw = fm.GetScalar("заказ") ?? "";
                if (!orderRaw.Contains($"[[{order}]]")) continue;
            }

            if (text is not null)
            {
                var haystack = string.Concat(
                    new[] { homePath, _paths.Request(sz), _paths.Findings(sz), _paths.Actions(sz) }
                        .Where(File.Exists).Select(File.ReadAllText));
                if (haystack.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
            }

            results.Add(new KbSearchResult(sz, fm.GetScalar("заказ") ?? "",
                fm.GetList("дефект"), fm.GetList("заменено")));
        }

        return results.OrderBy(r => r.Sz, StringComparer.Ordinal).ToList();
    }
}
