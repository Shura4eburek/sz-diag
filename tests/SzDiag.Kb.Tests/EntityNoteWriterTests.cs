using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class EntityNoteWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szent-{Guid.NewGuid():N}");
    private EntityNoteWriter NewWriter() => new(new KbPaths(_root));

    [Fact]
    public void EnsureOrder_CreatesNoteWithDataview()
    {
        var w = NewWriter();
        w.EnsureOrder("A-2025-0098");

        var text = File.ReadAllText(new KbPaths(_root).OrderNote("A-2025-0098"));
        Assert.Contains("# Замовлення A-2025-0098", text);
        Assert.Contains("```dataview", text);
    }

    [Fact]
    public void EnsureComponent_ExistingNote_NotOverwritten()
    {
        var w = NewWriter();
        var path = new KbPaths(_root).ComponentNote("SSD");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "РУЧНОЙ");

        w.EnsureComponent("SSD");

        Assert.Equal("РУЧНОЙ", File.ReadAllText(path));
    }

    [Fact]
    public void EnsureSymptom_CreatesNoteWithTypeAndDataview()
    {
        var w = NewWriter();
        w.EnsureSymptom("Фризи під навантаженням");

        var text = File.ReadAllText(new KbPaths(_root).SymptomNote("Фризи під навантаженням"));
        Assert.Contains("тип: симптом", text);
        Assert.Contains("# Симптом: Фризи під навантаженням", text);
        Assert.Contains("```dataview", text);
    }

    [Fact]
    public void EnsureSymptom_ExistingNote_NotOverwritten()
    {
        var w = NewWriter();
        var path = new KbPaths(_root).SymptomNote("Перегрев");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "МОИ ПРИЧИНЫ");

        w.EnsureSymptom("Перегрев");

        Assert.Equal("МОИ ПРИЧИНЫ", File.ReadAllText(path));
    }

    [Fact]
    public void EnsureDevice_WithSlashes_CreatesFileWithoutThrowing()
    {
        var w = NewWriter();
        w.EnsureDevice("ПК ASUS / Ryzen 5 / RTX 5060");

        Assert.True(File.Exists(new KbPaths(_root).DeviceNote("ПК ASUS / Ryzen 5 / RTX 5060")));
    }

    [Fact]
    public void EnsureMoc_CreatesMoc()
    {
        var w = NewWriter();
        w.EnsureMoc();
        Assert.True(File.Exists(new KbPaths(_root).Moc));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
