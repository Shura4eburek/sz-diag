namespace SzDiag.Kb;

/// <summary>
/// Создаёт каркас kb/СЗ/&lt;sz&gt;/ в Obsidian-форме. Идемпотентно: если папка СЗ
/// уже есть — ничего не трогает (данные диагностики не перетираются).
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
        if (Directory.Exists(dir)) return dir;

        Directory.CreateDirectory(_paths.LogsDir(sz));

        var date = _now().ToString("yyyy-MM-dd");
        WriteIfMissing(_paths.HomeNote(sz), HomeNote(sz, date));
        WriteIfMissing(_paths.Request(sz), $"# Дефект (со слов клиента) — СЗ {sz}\n\n");
        WriteIfMissing(_paths.Findings(sz), $"# Диагностика — СЗ {sz}\n\n");
        WriteIfMissing(_paths.Actions(sz), $"# Что заменили / сделали — СЗ {sz}\n\n");
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
        # Вывод по СЗ {sz}

        ## 📞 Для клиента
        > Простым языком, без терминов. Копируется в колл-центр как есть.

        **Что с устройством:** …
        **Что сделали:** …
        **Итог / рекомендации:** …

        ---

        ## 🔧 Технический разбор
        *для сервиса и обучения — клиенту не отдаётся*

        **Симптом (со слов клиента):** …

        **Показания диагностики:**
        - …
        - 🔗 сырой прогон: [[report]]

        **Рассуждение:** …

        **Диагноз:** …

        **Что помогло:** …

        **Паттерн:** [[симптом]]

        """;

    private static string HomeNote(string sz, string date) =>
        $"""
        ---
        сз: {sz}
        заказ: ""
        дефект: []
        заменено: []
        устройство: ""
        симптом: []
        статус: ""
        вердикт: ""
        дата: {date}
        ---

        # СЗ {sz}

        ## Вывод
        ![[вывод]]

        ## Дефект
        ![[request]]

        ## Диагностика
        ![[findings]]

        ## Замены
        ![[actions]]

        """;
}
