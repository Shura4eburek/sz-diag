using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class KbSearcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szsearch-{Guid.NewGuid():N}");
    private readonly KbPaths _paths;

    public KbSearcherTests()
    {
        _paths = new KbPaths(_root);
        var scaffolder = new KnowledgeBaseScaffolder(_root,
            () => new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var rec = new KbRecorder(_paths, scaffolder, new EntityNoteWriter(_paths),
            () => new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        rec.Record(new RecordRequest { Sz = "156864", Order = "A-1",
            Defects = new[] { "Не стартует POST" }, Findings = new[] { "нет видеосигнала" } });
        rec.Record(new RecordRequest { Sz = "156900", Order = "A-1",
            Defects = new[] { "Перегрев" } });
        rec.Record(new RecordRequest { Sz = "157000", Order = "B-2",
            Defects = new[] { "Синий экран" } });
    }

    [Fact]
    public void Search_ByOrder_ReturnsMatchingSz()
    {
        var results = new KbSearcher(_paths).Search(order: "A-1", text: null);
        Assert.Equal(new[] { "156864", "156900" }, results.Select(r => r.Sz).ToArray());
    }

    [Fact]
    public void Search_ByText_MatchesNoteContent()
    {
        var results = new KbSearcher(_paths).Search(order: null, text: "видеосигнал");
        var r = Assert.Single(results);
        Assert.Equal("156864", r.Sz);
    }

    [Fact]
    public void Search_OrderAndText_CombinedAnd()
    {
        var results = new KbSearcher(_paths).Search(order: "A-1", text: "перегрев");
        var r = Assert.Single(results);
        Assert.Equal("156900", r.Sz);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(new KbSearcher(_paths).Search(order: "Z-9", text: null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
