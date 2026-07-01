using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>Пишет файлы отчёта в kb через KbPaths. Имя файла санитизируется
/// (только имя, без каталогов) — защита от выхода за пределы reports/.</summary>
public sealed class KbReportStore : IReportStore
{
    private readonly KbPaths _paths;
    public KbReportStore(string kbRoot) => _paths = new KbPaths(kbRoot);

    public string Save(string sz, string timestamp, string fileName, byte[] content)
    {
        var safeName = Path.GetFileName(fileName);
        var dir = _paths.ReportDir(sz, timestamp);
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, safeName);
        File.WriteAllBytes(full, content);
        return full;
    }
}
