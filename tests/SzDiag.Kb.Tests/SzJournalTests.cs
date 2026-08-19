using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class SzJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szjrn-{Guid.NewGuid():N}");
    private readonly KbPaths _paths;

    public SzJournalTests() => _paths = new KbPaths(_root);

    private static DateTimeOffset At(int day, int hour, int min) =>
        new(2026, 8, day, hour, min, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Append_FirstEntry_WritesTitleDayHeaderAndLine()
    {
        var journal = new SzJournal(_paths);

        journal.Append("160697", new JournalEntry(At(10, 17, 4), JournalSource.Command,
            "`test run occt` — старт"));

        var text = File.ReadAllText(_paths.Journal("160697"));
        Assert.Contains("# Журнал 160697", text);
        Assert.Contains("## 2026-08-10", text);
        Assert.Contains("- **17:04** `test run occt` — старт", text);
    }

    [Fact]
    public void Append_SecondEntrySameDay_DoesNotDuplicateDayHeader()
    {
        var journal = new SzJournal(_paths);

        journal.Append("160697", new JournalEntry(At(10, 17, 4), JournalSource.Command, "перша"));
        journal.Append("160697", new JournalEntry(At(10, 17, 21), JournalSource.Machine, "друга"));

        var text = File.ReadAllText(_paths.Journal("160697"));
        Assert.Single(text.Split("## 2026-08-10")[1..]);
        Assert.Contains("- **17:21** ⚡ друга", text);
    }

    [Fact]
    public void Append_NextDay_AddsNewDayHeader()
    {
        var journal = new SzJournal(_paths);

        journal.Append("160697", new JournalEntry(At(10, 17, 4), JournalSource.Command, "перша"));
        journal.Append("160697", new JournalEntry(At(11, 9, 30), JournalSource.Manual, "друга"));

        var text = File.ReadAllText(_paths.Journal("160697"));
        Assert.Contains("## 2026-08-10", text);
        Assert.Contains("## 2026-08-11", text);
        Assert.Contains("- **09:30** ✋ друга", text);
    }

    [Fact]
    public void Append_CyrillicAndMarkdown_SurviveRoundTrip()
    {
        var journal = new SzJournal(_paths);

        journal.Append("160697", new JournalEntry(At(10, 17, 38), JournalSource.Manual,
            "майстер зняв **Gigabyte UD850GM**, поставив тестовий Corsair RM850x"));

        var text = File.ReadAllText(_paths.Journal("160697"));
        Assert.Contains("майстер зняв **Gigabyte UD850GM**, поставив тестовий Corsair RM850x", text);
    }

    [Fact]
    public void LastEntryAt_NoFile_ReturnsNull()
    {
        Assert.Null(new SzJournal(_paths).LastEntryAt("160697"));
    }

    [Fact]
    public void LastEntryAt_ReturnsMomentOfLastLine()
    {
        var journal = new SzJournal(_paths);
        journal.Append("160697", new JournalEntry(At(10, 17, 4), JournalSource.Command, "перша"));
        journal.Append("160697", new JournalEntry(At(11, 9, 30), JournalSource.Manual, "друга"));

        var last = journal.LastEntryAt("160697");

        Assert.Equal(new DateTime(2026, 8, 11, 9, 30, 0), last!.Value.DateTime);
    }

    [Fact]
    public void Tail_ReturnsLastEntriesInOrder_EvenIfFewerThanRequested()
    {
        var journal = new SzJournal(_paths);
        journal.Append("160697", new JournalEntry(At(10, 17, 4), JournalSource.Command, "перша"));
        journal.Append("160697", new JournalEntry(At(10, 17, 21), JournalSource.Machine, "друга"));

        var tail = journal.Tail("160697", 5);

        Assert.Equal(2, tail.Count);
        Assert.Equal("перша", tail[0].Text);
        Assert.Equal(JournalSource.Machine, tail[1].Source);
    }

    [Fact]
    public void Tail_LongerJournal_ReturnsOnlyRequestedCount()
    {
        var journal = new SzJournal(_paths);
        for (var i = 0; i < 5; i++)
            journal.Append("160697", new JournalEntry(At(10, 17, i), JournalSource.Command, $"крок {i}"));

        var tail = journal.Tail("160697", 2);

        Assert.Equal(2, tail.Count);
        Assert.Equal("крок 3", tail[0].Text);
        Assert.Equal("крок 4", tail[1].Text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
