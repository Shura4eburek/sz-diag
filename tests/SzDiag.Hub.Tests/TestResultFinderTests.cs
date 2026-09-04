using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Ищет свежий артефакт прогона (`occt-report.html`) в одном из двух мест —
/// `kb/СЗ/<номер>/reports/` или `Hub.PullRoot/<СЗ>/reports/` (`KbReportStore`: не-.md файлы
/// уходят вне vault, бэклог п.131). `szcli test result` (бэклог п.124/#60) не обязан знать,
/// куда именно уехал конкретный файл.</summary>
public class TestResultFinderTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sztestresult-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void FindLatestArtifact_InKbReportsDir_Found()
    {
        var root = TempDir();
        try
        {
            var reportsDir = Path.Combine(root, "kb", "СЗ", "156864", "reports", "20260904-120000");
            Directory.CreateDirectory(reportsDir);
            var file = Path.Combine(reportsDir, "occt-report.html");
            File.WriteAllText(file, "<html></html>");

            var found = TestResultFinder.FindLatestArtifact("156864", "occt-report.html",
                Path.Combine(root, "kb"), pullRoot: null);

            Assert.Equal(file, found);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindLatestArtifact_InPullRoot_FoundWhenNotInKb()
    {
        var root = TempDir();
        try
        {
            var pullDir = Path.Combine(root, "pulled", "156864", "reports", "20260904-120000");
            Directory.CreateDirectory(pullDir);
            var file = Path.Combine(pullDir, "occt-report.html");
            File.WriteAllText(file, "<html></html>");

            var found = TestResultFinder.FindLatestArtifact("156864", "occt-report.html",
                Path.Combine(root, "kb-empty"), Path.Combine(root, "pulled"));

            Assert.Equal(file, found);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindLatestArtifact_MultipleRuns_PicksNewestByWriteTime()
    {
        var root = TempDir();
        try
        {
            var kbRoot = Path.Combine(root, "kb");
            var older = Path.Combine(kbRoot, "СЗ", "156864", "reports", "20260901-100000");
            var newer = Path.Combine(kbRoot, "СЗ", "156864", "reports", "20260904-120000");
            Directory.CreateDirectory(older);
            Directory.CreateDirectory(newer);
            var olderFile = Path.Combine(older, "occt-report.html");
            var newerFile = Path.Combine(newer, "occt-report.html");
            File.WriteAllText(olderFile, "old");
            File.SetLastWriteTimeUtc(olderFile, DateTime.UtcNow.AddDays(-3));
            File.WriteAllText(newerFile, "new");
            File.SetLastWriteTimeUtc(newerFile, DateTime.UtcNow);

            var found = TestResultFinder.FindLatestArtifact("156864", "occt-report.html", kbRoot, pullRoot: null);

            Assert.Equal(newerFile, found);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FindLatestArtifact_NothingAnywhere_ReturnsNull()
    {
        var root = TempDir();
        try
        {
            var found = TestResultFinder.FindLatestArtifact("156864", "occt-report.html",
                Path.Combine(root, "kb"), Path.Combine(root, "pulled"));

            Assert.Null(found);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
