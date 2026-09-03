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

        // `діагностика.md` иначе так и остаётся пустой заглушкой на 42 байта — файл, который
        // логично открыть первым, не содержит ничего, хотя прогон давно завершился и отчёт
        // лежит в reports\<timestamp>\ (бэклог п.60). Маркеры — чтобы не съесть ручные правки
        // мастера, если он позже дописал в этот файл что-то своё.
        if (safeName.Equals("diag.md", StringComparison.OrdinalIgnoreCase))
            UpdateFindings(sz, timestamp, content.Length);

        return full;
    }

    private const string DiagBegin = "<!-- diag:початок -->";
    private const string DiagEnd = "<!-- diag:кінець -->";

    private void UpdateFindings(string sz, string timestamp, int bytes)
    {
        var path = _paths.Findings(sz);
        var existing = File.Exists(path) ? File.ReadAllText(path) : $"# Діагностика — СЗ {sz}\n\n";
        var block = $"{DiagBegin}\n**Останній прогін діагностики:** " +
                    $"[diag.md](reports/{timestamp}/diag.md) — {bytes / 1024d:N1} КБ, {DateTime.Now:dd.MM HH:mm}\n{DiagEnd}";

        var start = existing.IndexOf(DiagBegin, StringComparison.Ordinal);
        var end = existing.IndexOf(DiagEnd, StringComparison.Ordinal);
        var updated = start >= 0 && end > start
            ? existing[..start] + block + existing[(end + DiagEnd.Length)..]
            : existing.TrimEnd('\n', ' ') + "\n\n" + block + "\n";
        File.WriteAllText(path, updated);
    }
}
