using System.Text.RegularExpressions;

namespace SzDiag.Kb;

/// <summary>Проверка целостности vault базы знаний.
///
/// Боль (бэклог п.11): в заметке СЗ **160467** отображался вывод по **159794**. Данные не
/// путались — просто `висновок.md` в своей папке не создавался, а Obsidian разрешает короткие
/// ссылки `![[висновок]]` по всему vault и подставляет первый попавшийся файл с тем же именем.
/// Выглядит как порча данных, ищется глазами и неделями. Затронуто было три СЗ.
///
/// Конкретный случай починен (скелет создаёт заготовку), но класс проблемы шире: любой
/// висячий эмбед подтянет чужой файл, а не выдаст ошибку.</summary>
public static class KbDoctor
{
    /// <summary>Эмбед `![[имя]]` — именно он тихо подставляет чужой файл.</summary>
    private static readonly Regex Embed = new(@"!\[\[([^\]|#]+)", RegexOptions.Compiled);

    /// <summary>Обычная ссылка `[[имя]]` (без `!`).</summary>
    private static readonly Regex Link = new(@"(?<!!)\[\[([^\]|#]+)", RegexOptions.Compiled);

    /// <param name="Severity">`error` — точно ломает заметку (подтянется чужой файл);
    /// `warn` — стоит посмотреть.</param>
    public sealed record Issue(string Severity, string Where, string What)
    {
        public bool IsError => Severity == "error";
    }

    /// <summary>Файлы, без которых заметка СЗ неполна.</summary>
    private static readonly string[] RequiredNoteNames = { "запит", "діагностика", "дії", "висновок" };

    /// <summary>Проверить vault. `--fix` здесь нет намеренно: чинит скелеты
    /// <see cref="KnowledgeBaseScaffolder.EnsureSkeleton"/>, а доктор только смотрит.</summary>
    public static IReadOnlyList<Issue> Check(string kbRoot)
    {
        var paths = new KbPaths(kbRoot);
        var issues = new List<Issue>();
        if (!Directory.Exists(paths.SzRoot)) return issues;

        // Дубли имён между папками СЗ: ровно то, из-за чего Obsidian подставляет чужой файл.
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var szDir in Directory.GetDirectories(paths.SzRoot).OrderBy(d => d))
        {
            var sz = Path.GetFileName(szDir);
            var files = Directory.GetFiles(szDir, "*.md");
            var localNames = files.Select(Path.GetFileNameWithoutExtension)
                .Where(n => n is not null).Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var name in localNames)
            {
                if (!byName.TryGetValue(name, out var owners)) byName[name] = owners = new List<string>();
                owners.Add(sz);
            }

            foreach (var required in RequiredNoteNames)
            {
                if (!localNames.Contains(required))
                    issues.Add(new Issue("error", sz, $"нет файла {required}.md — эмбед подтянет чужой"));
            }

            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                var where = $"{sz}/{Path.GetFileName(file)}";

                foreach (Match m in Embed.Matches(text))
                {
                    var target = m.Groups[1].Value.Trim();
                    if (localNames.Contains(target)) continue;
                    issues.Add(new Issue("error", where,
                        $"эмбед ![[{target}]] без файла в своей папке — Obsidian подставит первый попавшийся в vault"));
                }

                foreach (Match m in Link.Matches(text))
                {
                    var target = m.Groups[1].Value.Trim();
                    if (localNames.Contains(target)) continue;
                    if (ExistsSomewhere(kbRoot, target)) continue;
                    issues.Add(new Issue("warn", where, $"ссылка [[{target}]] в никуда"));
                }
            }
        }

        foreach (var (name, owners) in byName.Where(kv => kv.Value.Count > 1))
        {
            // Одинаковые имена внутри СЗ — норма (запит.md есть у всех). Проблема в том, что
            // короткая ссылка на такое имя неоднозначна: её резолв зависит от порядка файлов.
            issues.Add(new Issue("warn", "vault",
                $"имя «{name}» встречается в {owners.Count} СЗ ({string.Join(", ", owners.Take(5))}…) — "
                + "короткие ссылки на него неоднозначны"));
        }

        return issues;
    }

    /// <summary>Есть ли такой .md где-нибудь в vault (сущности, паттерны, журнал).</summary>
    private static bool ExistsSomewhere(string kbRoot, string name)
    {
        try
        {
            var safe = KbPaths.SafeEntityName(name);
            return Directory.EnumerateFiles(kbRoot, $"{safe}.md", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return true;   // не смогли проверить — не выдумываем проблему
        }
    }
}
