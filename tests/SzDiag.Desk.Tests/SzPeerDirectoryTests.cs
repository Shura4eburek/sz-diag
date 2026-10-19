using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Kb;

namespace SzDiag.Desk.Tests;

public class SzPeerDirectoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly ChatHarness _h = new();
    private readonly KbPaths _kb;

    public SzPeerDirectoryTests() => _kb = new KbPaths(Path.Combine(_h.Dir, "kb"));

    public void Dispose() => _h.Dispose();

    private static SessionInfo S(string sz) => new(sz, "10.0.0.5", "PC", SessionStatus.Online, Now, Now, BootTime: Now);

    private async Task<SzPeerDirectory> New(params string[] live)
    {
        var hw = new HwProfileCache(new FakeHubApi { Exec = (_, _) => new ExecResult("r", 0, HwProfileTests.Tuf9800, "") },
            new ManualClock());
        foreach (var _ in live) await hw.Update(live.Select(S).ToList());
        return new SzPeerDirectory(_kb, hw, _h.Services.Sessions);
    }

    [Fact]
    public async Task Peers_OthersWithSession_WithSimilarity()
    {
        _h.Services.Sessions.Create("161432");
        _h.Services.Sessions.Create("161501");
        var dir = await New("161432", "161501", "161600");   // у 161600 сессии Desk нет

        var peer = Assert.Single(dir.Peers("161432"));
        Assert.Equal("161501", peer.Key);
        Assert.Contains("Ryzen 7 9800X3D", peer.Profile);
        Assert.Contains("память 2×32 ГБ @ 6000", peer.Similar);
    }

    [Fact]
    public async Task Summary_FindingsAndJournalTail()
    {
        Directory.CreateDirectory(_kb.SzDir("161501"));
        File.WriteAllText(_kb.Findings("161501"), "EXPO 6000 не держится");
        File.WriteAllLines(_kb.Journal("161501"), Enumerable.Range(1, 40).Select(i => $"строка {i}"));
        var text = (await New()).Summary("161501")!;

        Assert.Contains("EXPO 6000 не держится", text);
        Assert.Contains("строка 40", text);
        Assert.DoesNotContain("строка 10\n", text);   // из журнала — только последние 30
    }

    [Fact]
    public async Task Summary_Nothing_Null() => Assert.Null((await New()).Summary("161501"));

    [Theory]
    [InlineData("..\\..\\secrets")]
    [InlineData("16150")]
    [InlineData("161501\\..\\161432")]
    public async Task Summary_RejectsNonSzKey(string key)
    {
        // Ключ приходит от Claude и становится путём на диске.
        Directory.CreateDirectory(_kb.SzDir("161432"));
        File.WriteAllText(_kb.Findings("161432"), "секрет");
        Assert.Null((await New()).Summary(key));
    }

    [Fact]
    public void Briefing_MentionsPeerTools()
    {
        var b = SzBriefing.For("161432", null);
        Assert.Contains("peers()", b);
        Assert.Contains("ask_peer", b);
    }

    [Fact]
    public void Options_PeerLimitsDefaults()
    {
        var o = new DeskOptions();
        Assert.Equal(20, o.PeerLivePerHour);
        Assert.Equal(5, o.PeerTimeoutMinutes);
    }
}
