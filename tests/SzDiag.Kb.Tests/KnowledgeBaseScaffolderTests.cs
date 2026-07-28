using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class KnowledgeBaseScaffolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szkb-{Guid.NewGuid():N}");

    private KnowledgeBaseScaffolder NewScaffolder()
        => new(_root, () => new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void EnsureSkeleton_CreatesExpectedFiles()
    {
        var s = NewScaffolder();
        var dir = s.EnsureSkeleton("156864");

        Assert.True(File.Exists(Path.Combine(dir, "156864.md")));
        Assert.True(File.Exists(Path.Combine(dir, "запит.md")));
        Assert.True(File.Exists(Path.Combine(dir, "діагностика.md")));
        Assert.True(File.Exists(Path.Combine(dir, "дії.md")));
        Assert.True(Directory.Exists(Path.Combine(dir, "logs")));
        // Без локального висновок.md эмбед ![[висновок]] в заметке СЗ утаскивает чужой
        // файл из другой СЗ (Obsidian ищет короткую ссылку по всему vault).
        Assert.True(File.Exists(Path.Combine(dir, "висновок.md")));
    }

    [Fact]
    public void EnsureSkeleton_ExistingDirWithoutSummary_CreatesIt()
    {
        var s = NewScaffolder();
        var dir = s.EnsureSkeleton("156864");
        File.Delete(Path.Combine(dir, "висновок.md"));

        s.EnsureSkeleton("156864");

        Assert.True(File.Exists(Path.Combine(dir, "висновок.md")));
    }

    [Fact]
    public void HomeNote_ContainsFrontmatterWithSzAndAutoDate()
    {
        var s = NewScaffolder();
        var dir = s.EnsureSkeleton("156864");

        var home = File.ReadAllText(Path.Combine(dir, "156864.md"));
        Assert.Contains("сз: 156864", home);
        Assert.Contains("дата: 2026-07-01", home);
        Assert.Contains("![[запит]]", home);
    }

    [Fact]
    public void HomeNote_ContainsNewFrontmatterKeysAndSummaryEmbed()
    {
        var s = NewScaffolder();
        var dir = s.EnsureSkeleton("156864");

        var home = File.ReadAllText(Path.Combine(dir, "156864.md"));
        Assert.Contains("симптом: []", home);
        Assert.Contains("статус: \"\"", home);
        Assert.Contains("вердикт: \"\"", home);
        Assert.Contains("![[висновок]]", home);
    }

    [Fact]
    public void EnsureSummarySkeleton_CreatesVyvodWithBothBlocks()
    {
        var s = NewScaffolder();
        var path = s.EnsureSummarySkeleton("156864");

        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("## 📞 Для клієнта", text);
        Assert.Contains("## 🔧 Технічний розбір", text);
    }

    [Fact]
    public void EnsureSummarySkeleton_ExistingFile_NotOverwritten()
    {
        var s = NewScaffolder();
        var path = s.EnsureSummarySkeleton("156864");
        File.WriteAllText(path, "МОЙ РАЗБОР");

        s.EnsureSummarySkeleton("156864");

        Assert.Equal("МОЙ РАЗБОР", File.ReadAllText(path));
    }

    [Fact]
    public void EnsureSkeleton_ExistingDir_DoesNotOverwrite()
    {
        var s = NewScaffolder();
        var dir = s.EnsureSkeleton("156864");
        var reqPath = Path.Combine(dir, "запит.md");
        File.WriteAllText(reqPath, "РУЧНОЙ ТЕКСТ");

        s.EnsureSkeleton("156864");

        Assert.Equal("РУЧНОЙ ТЕКСТ", File.ReadAllText(reqPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
