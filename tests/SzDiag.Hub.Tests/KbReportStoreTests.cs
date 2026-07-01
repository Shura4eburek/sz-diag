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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
