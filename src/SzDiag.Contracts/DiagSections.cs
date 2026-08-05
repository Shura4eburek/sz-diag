namespace SzDiag.Contracts;

/// <summary>Имена секций диагностики — общий словарь для CLI (валидация ввода) и агента
/// (каталог проб). Лежит в Contracts по той же причине, что и <see cref="HubRoutes"/>:
/// иначе CLI молча пропускает опечатку, а агент собирает не то.
///
/// Боль, из-за которой это появилось (бэклог п.6): <c>szcli diag run 160306 hw memory reboots
/// whea</c> отрабатывал как «одна секция system» — пробел резал аргументы, а несуществующие
/// имена игнорировались без единого слова.</summary>
public static class DiagSections
{
    /// <summary>Канонические имена секций. Порядок — как в отчёте.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        "system", "cpu", "memory", "gpu", "storage",
        "temps", "drivers", "events", "reboots", "whea", "reliability", "battery"
    };

    /// <summary>Привычные синонимы: так секции зовут в переписке и в голове, и именно
    /// в таком виде они уже уезжали в CLI (и молча игнорировались).</summary>
    public static IReadOnlyDictionary<string, string> Aliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hw"] = "system",
            ["os"] = "system",
            ["ram"] = "memory",
            ["disks"] = "storage",
            ["disk"] = "storage",
            ["smart"] = "storage",
            ["video"] = "gpu",
            ["reboot"] = "reboots",
            ["bsod"] = "reliability",
            ["minidump"] = "reliability",
            ["minidumps"] = "reliability",
            ["temp"] = "temps",
            ["temperature"] = "temps",
            ["event"] = "events",
        };

    /// <summary>Разбирает пользовательский ввод в список канонических секций.
    /// Принимает и запятые, и несколько аргументов через пробел, и <c>all</c>.</summary>
    /// <param name="tokens">Аргументы командной строки после номера СЗ.</param>
    /// <returns>Канонические секции без дублей (порядок ввода) и список нераспознанных имён.
    /// Пустой ввод — <c>Sections = null</c>, что означает «все секции».</returns>
    public static (IReadOnlyList<string>? Sections, IReadOnlyList<string> Unknown) Parse(IEnumerable<string> tokens)
    {
        var raw = tokens
            .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        if (raw.Count == 0) return (null, Array.Empty<string>());
        if (raw.Any(t => t.Equals("all", StringComparison.OrdinalIgnoreCase)))
            return (null, Array.Empty<string>());

        var sections = new List<string>();
        var unknown = new List<string>();
        foreach (var token in raw)
        {
            var name = Aliases.TryGetValue(token, out var canon) ? canon : token.ToLowerInvariant();
            if (!All.Contains(name)) { unknown.Add(token); continue; }
            if (!sections.Contains(name)) sections.Add(name);
        }
        return (sections, unknown);
    }
}
