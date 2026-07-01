namespace SzDiag.Hub;

/// <summary>
/// Создаёт каркас kb/СЗ/&lt;sz&gt;/ в Obsidian-форме. Идемпотентно: если папка СЗ
/// уже есть — ничего не трогает (данные диагностики не перетираются).
/// </summary>
public sealed class KnowledgeBaseScaffolder : IKnowledgeBaseScaffolder
{
    private readonly string _kbRoot;
    private readonly Func<DateTimeOffset> _now;

    public KnowledgeBaseScaffolder(string kbRoot, Func<DateTimeOffset>? now = null)
    {
        _kbRoot = kbRoot;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string EnsureSkeleton(string sz)
    {
        var dir = Path.Combine(_kbRoot, "СЗ", sz);
        if (Directory.Exists(dir)) return dir;

        Directory.CreateDirectory(Path.Combine(dir, "logs"));

        var date = _now().ToString("yyyy-MM-dd");
        WriteIfMissing(Path.Combine(dir, $"{sz}.md"), HomeNote(sz, date));
        WriteIfMissing(Path.Combine(dir, "request.md"), $"# Дефект (со слов клиента) — СЗ {sz}\n\n");
        WriteIfMissing(Path.Combine(dir, "findings.md"), $"# Диагностика — СЗ {sz}\n\n");
        WriteIfMissing(Path.Combine(dir, "actions.md"), $"# Что заменили / сделали — СЗ {sz}\n\n");
        return dir;
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    private static string HomeNote(string sz, string date) =>
        $"""
        ---
        сз: {sz}
        заказ: ""
        дефект: []
        заменено: []
        устройство: ""
        дата: {date}
        ---

        # СЗ {sz}

        ## Дефект
        ![[request]]

        ## Диагностика
        ![[findings]]

        ## Замены
        ![[actions]]

        """;
}
