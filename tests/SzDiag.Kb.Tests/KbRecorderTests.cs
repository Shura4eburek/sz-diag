using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class KbRecorderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szrec-{Guid.NewGuid():N}");
    private readonly KbPaths _paths;

    public KbRecorderTests() => _paths = new KbPaths(_root);

    private KbRecorder NewRecorder()
    {
        var scaffolder = new KnowledgeBaseScaffolder(_root,
            () => new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        return new KbRecorder(_paths, scaffolder, new EntityNoteWriter(_paths),
            () => new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Record_NewSz_CreatesSkeletonAndFillsFrontmatter()
    {
        NewRecorder().Record(new RecordRequest
        {
            Sz = "156864",
            Order = "A-2025-0098",
            Device = "Lenovo IdeaPad 3",
            Defects = new[] { "Не стартует POST" },
            Replaced = new[] { "Kingston A400 240GB" }
        });

        var home = File.ReadAllText(_paths.HomeNote("156864"));
        Assert.Contains("заказ: \"[[A-2025-0098]]\"", home);
        Assert.Contains("устройство: \"[[Lenovo IdeaPad 3]]\"", home);
        Assert.Contains("дефект: [\"[[Не стартует POST]]\"]", home);
        Assert.Contains("заменено: [\"[[Kingston A400 240GB]]\"]", home);
    }

    [Fact]
    public void Record_CreatesLinkedEntityNotes()
    {
        NewRecorder().Record(new RecordRequest
        {
            Sz = "156864",
            Order = "A-1",
            Defects = new[] { "Перегрев" },
            Replaced = new[] { "Кулер" }
        });

        Assert.True(File.Exists(_paths.OrderNote("A-1")));
        Assert.True(File.Exists(_paths.DefectNote("Перегрев")));
        Assert.True(File.Exists(_paths.ComponentNote("Кулер")));
    }

    [Fact]
    public void Record_AppendsFindingsAndActionsWithDate()
    {
        NewRecorder().Record(new RecordRequest
        {
            Sz = "156864",
            Findings = new[] { "Нет видеосигнала" },
            Actions = new[] { "Заменён SSD" }
        });

        Assert.Contains("- 2026-07-01: Нет видеосигнала", File.ReadAllText(_paths.Findings("156864")));
        Assert.Contains("- 2026-07-01: Заменён SSD", File.ReadAllText(_paths.Actions("156864")));
    }

    [Fact]
    public void Record_SymptomStatusVerdict_MergedAndSymptomNoteCreated()
    {
        NewRecorder().Record(new RecordRequest
        {
            Sz = "156864",
            Symptoms = new[] { "Фризы под нагрузкой" },
            Status = "готово",
            Verdict = "подтверждён"
        });

        var home = File.ReadAllText(_paths.HomeNote("156864"));
        Assert.Contains("симптом: [\"[[Фризы под нагрузкой]]\"]", home);
        Assert.Contains("статус: готово", home);
        Assert.Contains("вердикт: подтверждён", home);
        Assert.True(File.Exists(_paths.SymptomNote("Фризы под нагрузкой")));
    }

    [Fact]
    public void Record_Twice_IsIdempotent()
    {
        var rec = NewRecorder();
        var req = new RecordRequest
        {
            Sz = "156864",
            Defects = new[] { "Не стартует POST" },
            Findings = new[] { "Нет видеосигнала" }
        };
        rec.Record(req);
        rec.Record(req);

        var home = File.ReadAllText(_paths.HomeNote("156864"));
        Assert.Equal("дефект: [\"[[Не стартует POST]]\"]",
            home.Split('\n').Single(l => l.StartsWith("дефект:")));

        var findings = File.ReadAllText(_paths.Findings("156864"));
        var count = findings.Split("- 2026-07-01: Нет видеосигнала").Length - 1;
        Assert.Equal(1, count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
