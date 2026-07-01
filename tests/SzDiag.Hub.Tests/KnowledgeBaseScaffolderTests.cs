using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

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
        Assert.True(File.Exists(Path.Combine(dir, "request.md")));
        Assert.True(File.Exists(Path.Combine(dir, "findings.md")));
        Assert.True(File.Exists(Path.Combine(dir, "actions.md")));
        Assert.True(Directory.Exists(Path.Combine(dir, "logs")));
    }

    [Fact]
    public void HomeNote_ContainsFrontmatterWithSzAndAutoDate()
    {
        var s = NewScaffolder();
        var dir = s.EnsureSkeleton("156864");

        var home = File.ReadAllText(Path.Combine(dir, "156864.md"));
        Assert.Contains("сз: 156864", home);
        Assert.Contains("дата: 2026-07-01", home);
        Assert.Contains("![[request]]", home);
    }

    [Fact]
    public void EnsureSkeleton_ExistingDir_DoesNotOverwrite()
    {
        var s = NewScaffolder();
        var dir = s.EnsureSkeleton("156864");
        var reqPath = Path.Combine(dir, "request.md");
        File.WriteAllText(reqPath, "РУЧНОЙ ТЕКСТ");

        s.EnsureSkeleton("156864"); // повторно

        Assert.Equal("РУЧНОЙ ТЕКСТ", File.ReadAllText(reqPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
