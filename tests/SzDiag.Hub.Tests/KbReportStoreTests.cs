using SzDiag.Hub;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Hub.Tests;

public class KbReportStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szrep-{Guid.NewGuid():N}");

    [Fact]
    public void Save_WritesFileUnderReportsTimestamp()
    {
        var store = new KbReportStore(_root);
        var path = store.Save("156864", "20260701-120000", "report.md", "hi"u8.ToArray());

        var expected = new KbPaths(_root).ReportDir("156864", "20260701-120000");
        Assert.Equal(Path.Combine(expected, "report.md"), path);
        Assert.Equal("hi", File.ReadAllText(path));
    }

    [Fact]
    public void Save_SanitizesTraversalInFileName()
    {
        var store = new KbReportStore(_root);
        var path = store.Save("156864", "ts", "../../evil.md", "x"u8.ToArray());

        Assert.EndsWith(Path.Combine("reports", "ts", "evil.md"), path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_HeavyArtifacts_GoToPullRootNotVault()
    {
        // Регрессия (бэклог п.131): HTML-отчёты OCCT (2,8–6,9 МБ) и скрины уезжали прямо в
        // vault — 34 МБ за одну заявку в git-истории базы знаний, kb-backup отбивал коммит.
        var pull = Path.Combine(_root, "pulled");
        var store = new KbReportStore(Path.Combine(_root, "kb"), pull);

        var md = store.Save("161346", "ts", "report.md", "text"u8.ToArray());
        var html = store.Save("161346", "ts", "occt-report.html", "<html>"u8.ToArray());
        var png = store.Save("161346", "ts", "screen-1.png", "png"u8.ToArray());

        Assert.Contains(Path.Combine("kb", "СЗ"), md);
        Assert.StartsWith(Path.Combine(pull, "161346", "reports", "ts"), html);
        Assert.StartsWith(Path.Combine(pull, "161346", "reports", "ts"), png);
        Assert.True(File.Exists(html));
    }

    [Fact]
    public void Save_WithoutPullRoot_KeepsOldBehaviour()
    {
        var store = new KbReportStore(_root);
        var path = store.Save("161346", "ts", "screen-1.png", "png"u8.ToArray());

        Assert.EndsWith(Path.Combine("reports", "ts", "screen-1.png"), path);
    }

    [Fact]
    public void Save_DiagMd_UpdatesFindingsWithLinkToLatestReport()
    {
        // Регрессия (бэклог п.60): діагностика.md оставалась пустой заглушкой (42 байта) даже
        // после успешного прогона — файл, который логично открыть первым, не содержал ничего.
        var store = new KbReportStore(_root);
        var content = "# diag\n\ncontent"u8.ToArray();

        store.Save("156864", "20260904-120000", "diag.md", content);

        var findingsPath = new KbPaths(_root).Findings("156864");
        var text = File.ReadAllText(findingsPath);
        Assert.Contains("reports/20260904-120000/diag.md", text);
        Assert.Contains("КБ", text);
    }

    [Fact]
    public void Save_DiagMd_Twice_ReplacesLinkInsteadOfDuplicating()
    {
        var store = new KbReportStore(_root);
        store.Save("156864", "20260904-120000", "diag.md", "first"u8.ToArray());
        store.Save("156864", "20260904-130000", "diag.md", "second"u8.ToArray());

        var text = File.ReadAllText(new KbPaths(_root).Findings("156864"));
        Assert.DoesNotContain("20260904-120000", text);
        Assert.Contains("20260904-130000", text);
    }

    [Fact]
    public void Save_DiagMd_PreservesHandwrittenTextOutsideMarkers()
    {
        var findingsPath = new KbPaths(_root).Findings("156864");
        Directory.CreateDirectory(Path.GetDirectoryName(findingsPath)!);
        File.WriteAllText(findingsPath, "# Діагностика — СЗ 156864\n\nручна нотатка майстра\n");

        var store = new KbReportStore(_root);
        store.Save("156864", "ts", "diag.md", "x"u8.ToArray());

        var text = File.ReadAllText(findingsPath);
        Assert.Contains("ручна нотатка майстра", text);
        Assert.Contains("reports/ts/diag.md", text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
