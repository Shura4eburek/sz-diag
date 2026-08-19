using Microsoft.Extensions.Logging.Abstractions;
using SzDiag.Hub;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Hub.Tests;

public class JournalWriterTests
{
    private sealed class FakeJournal : ISzJournal
    {
        public List<(string Sz, JournalEntry Entry)> Written { get; } = new();
        public Exception? Throw { get; set; }

        public void Append(string sz, JournalEntry entry)
        {
            if (Throw is not null) throw Throw;
            Written.Add((sz, entry));
        }

        public DateTimeOffset? LastEntryAt(string sz) => null;
        public IReadOnlyList<JournalEntry> Tail(string sz, int count) => Array.Empty<JournalEntry>();
    }

    private sealed class FakeScaffolder : IKnowledgeBaseScaffolder
    {
        public List<string> Ensured { get; } = new();

        public string EnsureSkeleton(string sz)
        {
            Ensured.Add(sz);
            return Path.Combine("kb", "СЗ", sz);
        }

        public string EnsureSummarySkeleton(string sz) => Path.Combine("kb", "СЗ", sz, "висновок.md");
    }

    private static JournalWriter NewWriter(FakeJournal journal, FakeScaffolder? scaffolder = null) =>
        new(journal, scaffolder ?? new FakeScaffolder(), NullLogger<JournalWriter>.Instance,
            () => new DateTimeOffset(2026, 8, 10, 17, 38, 0, TimeSpan.FromHours(3)));

    [Fact]
    public void Manual_WritesEntryWithManualSource_AndEnsuresSkeleton()
    {
        var journal = new FakeJournal();
        var scaffolder = new FakeScaffolder();

        NewWriter(journal, scaffolder).Manual("160697", "поставив тестовий Corsair RM850x");

        var (sz, entry) = Assert.Single(journal.Written);
        Assert.Equal("160697", sz);
        Assert.Equal(JournalSource.Manual, entry.Source);
        Assert.Equal("поставив тестовий Corsair RM850x", entry.Text);
        Assert.Equal("160697", Assert.Single(scaffolder.Ensured));
    }

    [Fact]
    public void Command_Machine_Snapshot_UseTheirOwnSources()
    {
        var journal = new FakeJournal();
        var writer = NewWriter(journal);

        writer.Command("160697", "`push occt` — доставка");
        writer.Machine("160697", "вирубон");
        writer.Snapshot("160697", "RAM 6000 → 4800");

        Assert.Equal(
            new[] { JournalSource.Command, JournalSource.Machine, JournalSource.Snapshot },
            journal.Written.Select(w => w.Entry.Source));
    }

    [Fact]
    public void Append_WhenJournalThrows_DoesNotPropagate()
    {
        var journal = new FakeJournal { Throw = new IOException("vault занят") };

        var ex = Record.Exception(() => NewWriter(journal).Command("160697", "`push occt` — принято"));

        Assert.Null(ex);
    }
}
