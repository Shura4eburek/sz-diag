using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class ToolCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sztools-{Guid.NewGuid():N}");

    public ToolCatalogTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "occt", "schedules"));
        File.WriteAllText(Path.Combine(_root, "occt", "OCCTCmd.exe"), "MZ...");
        File.WriteAllText(Path.Combine(_root, "occt", "schedules", "long.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "tm5"));
        File.WriteAllText(Path.Combine(_root, "tm5", "TM5.exe"), "MZ");
        // Секрет рядом с каталогом — раздавать его нельзя ни при каких путях.
        File.WriteAllText(Path.Combine(_root, "секрет.key"), "ssh-ed25519 PRIVATE");
    }

    [Fact]
    public void List_ReturnsToolsWithCountsAndSizes()
    {
        var tools = new ToolCatalog(_root).List();

        Assert.Equal(2, tools.Count);
        var occt = tools.Single(t => t.Name == "occt");
        Assert.Equal(2, occt.Files);          // включая вложенную папку
        Assert.True(occt.Bytes > 0);
    }

    [Fact]
    public void Manifest_ListsNestedFilesWithForwardSlashes()
    {
        var manifest = new ToolCatalog(_root).Manifest("occt");

        Assert.NotNull(manifest);
        Assert.Contains(manifest!.Files, f => f.Path == "schedules/long.json");
        Assert.All(manifest.Files, f => Assert.Equal(64, f.Sha256.Length));
        Assert.DoesNotContain(manifest.Files, f => f.Path.Contains('\\'));
    }

    [Fact]
    public void Manifest_UnknownTool_ReturnsNull()
        => Assert.Null(new ToolCatalog(_root).Manifest("нет-такого"));

    [Theory]
    [InlineData("../секрет.key")]
    [InlineData("..\\секрет.key")]
    [InlineData("schedules/../../секрет.key")]
    public void ResolveFile_EscapeAttempt_Denied(string path)
    {
        // Раздача не должна отдавать ничего за пределами папки инструмента.
        Assert.Null(new ToolCatalog(_root).ResolveFile("occt", path));
    }

    [Theory]
    [InlineData("../occt")]
    [InlineData("occt/schedules")]
    public void ResolveFile_ToolNameWithPath_Denied(string tool)
        => Assert.Null(new ToolCatalog(_root).ResolveFile(tool, "OCCTCmd.exe"));

    [Fact]
    public void ResolveFile_NormalPath_Resolved()
    {
        var full = new ToolCatalog(_root).ResolveFile("occt", "schedules/long.json");

        Assert.NotNull(full);
        Assert.True(File.Exists(full));
    }

    [Fact]
    public void Manifest_UnchangedFile_ReusesCachedSha()
    {
        // sha256 по 300-мегабайтному OCCT на каждый запрос манифеста — впустую прочитанные
        // сотни мегабайт; кэш обязан отдавать тот же результат.
        var catalog = new ToolCatalog(_root);
        var first = catalog.Manifest("occt")!;
        var second = catalog.Manifest("occt")!;

        Assert.Equal(first.Files.Select(f => f.Sha256), second.Files.Select(f => f.Sha256));
    }

    [Fact]
    public void Manifest_FileChanged_ShaRecomputed()
    {
        var catalog = new ToolCatalog(_root);
        var before = catalog.Manifest("tm5")!.Files.Single().Sha256;

        var path = Path.Combine(_root, "tm5", "TM5.exe");
        File.WriteAllText(path, "MZ-другой-билд");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        Assert.NotEqual(before, catalog.Manifest("tm5")!.Files.Single().Sha256);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
