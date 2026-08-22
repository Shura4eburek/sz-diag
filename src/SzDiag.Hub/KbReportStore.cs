using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>Пишет файлы отчёта в kb через KbPaths. Имя файла санитизируется
/// (только имя, без каталогов) — защита от выхода за пределы reports/.
///
/// Тяжёлые артефакты (всё, кроме .md) уходят в PullRoot, а не в vault: HTML-отчёты OCCT
/// по 2,8–6,9 МБ и скрины раздували git-историю базы знаний необратимо — 34 МБ за одну
/// заявку, kb-backup отбивал коммит (бэклог п.131; правило CLAUDE.md «в vault — только
/// заметки и ссылки» нарушал сам агент на каждом прогоне).</summary>
public sealed class KbReportStore : IReportStore
{
    private readonly KbPaths _paths;
    private readonly string? _pullRoot;

    /// <param name="pullRoot">Каталог для артефактов вне vault (обычно `Hub.PullRoot`);
    /// null — всё в kb, как раньше (тестовые сценарии).</param>
    public KbReportStore(string kbRoot, string? pullRoot = null)
    {
        _paths = new KbPaths(kbRoot);
        _pullRoot = string.IsNullOrWhiteSpace(pullRoot) ? null : pullRoot;
    }

    public string Save(string sz, string timestamp, string fileName, byte[] content)
    {
        var safeName = Path.GetFileName(fileName);
        var isNote = safeName.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var dir = isNote || _pullRoot is null
            ? _paths.ReportDir(sz, timestamp)
            : Path.Combine(_pullRoot, sz, "reports", timestamp);
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, safeName);
        File.WriteAllBytes(full, content);
        return full;
    }
}
