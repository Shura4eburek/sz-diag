using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>Находит самый свежий артефакт прогона (напр. <c>occt-report.html</c>) по СЗ —
/// он лежит в одном из двух мест в зависимости от того, настроен ли <c>Hub.PullRoot</c>
/// (`KbReportStore`: не-.md файлы уходят вне vault, бэклог п.131). `szcli test result`
/// (бэклог п.124/#60) не обязан знать, куда именно — ищет по обоим кандидатам.</summary>
public static class TestResultFinder
{
    public static string? FindLatestArtifact(string sz, string fileName, string kbRoot, string? pullRoot)
    {
        var candidates = new List<string>();

        var kbReportsDir = new KbPaths(kbRoot).ReportsDir(sz);
        if (Directory.Exists(kbReportsDir)) candidates.Add(kbReportsDir);

        if (!string.IsNullOrWhiteSpace(pullRoot))
        {
            var root = Path.IsPathRooted(pullRoot) ? pullRoot : Path.Combine(AppContext.BaseDirectory, pullRoot);
            var pullReportsDir = Path.Combine(root, sz, "reports");
            if (Directory.Exists(pullReportsDir)) candidates.Add(pullReportsDir);
        }

        return candidates
            .SelectMany(d => Directory.Exists(d) ? Directory.GetFiles(d, fileName, SearchOption.AllDirectories) : Array.Empty<string>())
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
