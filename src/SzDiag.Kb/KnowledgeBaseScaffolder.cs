namespace SzDiag.Kb;

/// <summary>
/// Создаёт каркас kb/СЗ/&lt;sz&gt;/ в Obsidian-форме. Идемпотентно: если папка СЗ
/// уже есть — ничего не трогает (данные диагностики не перетираются).
/// Контент базы знаний — на украинском (сервис/колл-центр украиноязычные).
/// </summary>
public sealed class KnowledgeBaseScaffolder : IKnowledgeBaseScaffolder
{
    private readonly KbPaths _paths;
    private readonly Func<DateTimeOffset> _now;

    public KnowledgeBaseScaffolder(string kbRoot, Func<DateTimeOffset>? now = null)
    {
        _paths = new KbPaths(kbRoot);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string EnsureSkeleton(string sz)
    {
        var dir = _paths.SzDir(sz);
        Directory.CreateDirectory(_paths.LogsDir(sz));

        var date = _now().ToString("yyyy-MM-dd");
        WriteIfMissing(_paths.HomeNote(sz), HomeNote(sz, date));
        WriteIfMissing(_paths.Request(sz), $"# Дефект (зі слів клієнта) — СЗ {sz}\n\n");
        WriteIfMissing(_paths.Findings(sz), $"# Діагностика — СЗ {sz}\n\n");
        WriteIfMissing(_paths.Actions(sz), $"# Що замінили / зробили — СЗ {sz}\n\n");
        // Заготовка висновку обязательна: HomeNote всегда содержит ![[висновок]], а Obsidian
        // разрешает короткие ссылки по всему vault — без локального файла в заметку СЗ
        // подтягивается чужой висновок из другой СЗ (ловили на 160467 → показывался 159794).
        WriteIfMissing(_paths.Summary(sz), SummaryNote(sz));
        EnsureTemplates();
        return dir;
    }

    /// <summary>Шаблоны в vault: ответ на жалобу собирался с нуля каждый раз, и половина
    /// времени уходила на формат, а не на факты (бэклог п.84).</summary>
    public string EnsureTemplates()
    {
        var dir = Path.Combine(_paths.Root, KbTemplates.FolderName);
        Directory.CreateDirectory(dir);
        WriteIfMissing(Path.Combine(dir, KbTemplates.ComplaintReplyFile), KbTemplates.ComplaintReply);
        return dir;
    }

    public string EnsureSummarySkeleton(string sz)
    {
        EnsureSkeleton(sz);
        var path = _paths.Summary(sz);
        WriteIfMissing(path, SummaryNote(sz));
        return path;
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    private static string SummaryNote(string sz) =>
        $"""
        # Висновок по СЗ {sz}

        ## 📞 Для клієнта
        > Простою мовою, без термінів. Копіюється в колл-центр як є.

        **Що з пристроєм:** …
        **Що зробили:** …
        **Підсумок / рекомендації:** …

        ---

        ## 🔧 Технічний розбір
        *для сервісу та навчання — клієнту не віддається*

        **Симптом (зі слів клієнта):** …

        **Показники діагностики:**
        - …
        - 🔗 сирий прогін: [[report]]

        **Міркування:** …

        **Діагноз:** …

        **Що допомогло:** …

        **Патерн:** [[симптом]]

        """;

    private static string HomeNote(string sz, string date) =>
        $"""
        ---
        сз: {sz}
        замовлення: ""
        дефект: []
        замінено: []
        пристрій: ""
        симптом: []
        статус: ""
        вердикт: ""
        дата: {date}
        ---

        # СЗ {sz}

        ## Висновок
        ![[висновок]]

        ## Дефект
        ![[запит]]

        ## Діагностика
        ![[діагностика]]

        ## Заміни
        ![[дії]]

        """;
}
