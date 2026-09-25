using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class StoresTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szclaude-" + Guid.NewGuid().ToString("N"));

    public StoresTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void Transcript_AppendAndLoad()
    {
        var t = new TranscriptStore(Path.Combine(_dir, "sessions"));
        t.Append("161432", DeskLines.Serialize(new DeskUserMessage("привет"))!);
        t.Append("161432", Fixture.Line("simple-turn.jsonl", e => e is AssistantText) + "\r\n");

        var ev = t.Load("161432");
        Assert.Equal("привет", Assert.IsType<DeskUserMessage>(ev[0]).Text);
        Assert.Equal("Привет", Assert.IsType<AssistantText>(ev[1]).Text);
        Assert.Empty(t.Load("161501"));
    }

    [Fact]
    public void Transcript_KeyWithBadChars_StaysInDir()
    {
        var t = new TranscriptStore(_dir);
        Assert.Equal(_dir, Path.GetDirectoryName(t.PathFor("..\\x:y")));
    }

    [Fact]
    public void Index_PutGetReload()
    {
        var path = Path.Combine(_dir, "desk-sessions.json");
        var idx = SessionIndex.Load(path);
        idx.Put(new SessionRecord("161501", null, DateTimeOffset.UnixEpoch, false));
        idx.Put(new SessionRecord("161432", "sid", DateTimeOffset.UnixEpoch, true));

        var again = SessionIndex.Load(path);
        Assert.Equal(new[] { "161432", "161501" }, again.All.Select(r => r.Key));
        Assert.Equal("sid", again.Get("161432")!.SessionId);
        Assert.True(again.Get("161432")!.Archived);
        Assert.Null(again.Get("999999"));
    }

    [Fact]
    public void Index_CorruptFile_Empty()
    {
        var path = Path.Combine(_dir, "desk-sessions.json");
        File.WriteAllText(path, "{битый");
        Assert.Empty(SessionIndex.Load(path).All);
    }

    [Fact]
    public void Ledger_SumsPersistsAndRollsOverAtMidnight()
    {
        var path = Path.Combine(_dir, "desk-tokens.json");
        var clock = new Clock();
        var l = new TokenLedger(path, clock);
        var changed = 0;
        l.Changed += () => changed++;
        l.Add(new TokenUsage(100, 20, 1000, 50), 0.10m);
        l.Add(new TokenUsage(1, 2, 3, 4), 0.02m);

        Assert.Equal(1180, l.Today.Total);
        Assert.Equal(0.12m, l.CostToday);
        Assert.Equal(2, changed);
        Assert.Equal(1180, new TokenLedger(path, clock).Today.Total);

        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(0, l.Today.Total);
        Assert.Equal(0m, l.CostToday);
    }

    [Fact]
    public void Ledger_CorruptFile_StartsFromZero()
    {
        var path = Path.Combine(_dir, "desk-tokens.json");
        File.WriteAllText(path, "мусор");
        Assert.Equal(0, new TokenLedger(path, new Clock()).Today.Total);
    }
}
