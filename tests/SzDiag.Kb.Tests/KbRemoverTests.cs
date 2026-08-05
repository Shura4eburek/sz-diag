using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class KbRemoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szkb-{Guid.NewGuid():N}");

    private KbPaths Paths => new(_root);

    private string Scaffold(string sz)
        => new KnowledgeBaseScaffolder(_root, () => new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero))
            .EnsureSkeleton(sz);

    [Fact]
    public void Remove_DeletesWholeSzFolderIncludingEmptyDirs()
    {
        // Мусорную СЗ раньше вычищали руками: git rm + добивание пустых каталогов,
        // которые git не убирает, а Obsidian продолжает показывать (бэклог п.57).
        var dir = Scaffold("111111");
        Directory.CreateDirectory(Path.Combine(dir, "reports", "20260731-120000"));
        File.WriteAllText(Path.Combine(dir, "reports", "20260731-120000", "diag.md"), "# отчёт");

        var result = new KbRemover(Paths).Remove("111111");

        Assert.True(result.Existed);
        Assert.True(result.FilesRemoved >= 5);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(Directory.GetDirectories(Paths.SzRoot));
    }

    [Fact]
    public void Remove_MissingSz_IsNotAnError()
    {
        Scaffold("160705");   // vault существует, но удаляем другую СЗ

        var result = new KbRemover(Paths).Remove("999999");

        Assert.False(result.Existed);
        Assert.Equal(0, result.FilesRemoved);
        Assert.True(Directory.Exists(Paths.SzDir("160705")));   // чужую не тронули
    }

    [Fact]
    public void Remove_ReportsIncomingLinksFromOtherNotes()
    {
        Scaffold("111111");
        Scaffold("160705");
        File.AppendAllText(Paths.Findings("160705"), "\nсхожий випадок: [[111111]]\n");
        Directory.CreateDirectory(Paths.SymptomsRoot);
        File.WriteAllText(Path.Combine(Paths.SymptomsRoot, "вимикається.md"), "кейси: [[111111|СЗ 111111]]");

        var result = new KbRemover(Paths).Remove("111111");

        Assert.Equal(2, result.IncomingLinks.Count);
        Assert.Contains(result.IncomingLinks, p => p.EndsWith("діагностика.md"));
        Assert.Contains(result.IncomingLinks, p => p.EndsWith("вимикається.md"));
    }

    [Fact]
    public void FindIncomingLinks_IgnoresLinksInsideOwnFolder()
    {
        // Заметки самой СЗ ссылаются друг на друга — это не «внешние» ссылки.
        Scaffold("111111");
        File.AppendAllText(Paths.Findings("111111"), "\nсм. [[111111]]\n");

        Assert.Empty(new KbRemover(Paths).FindIncomingLinks("111111"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
