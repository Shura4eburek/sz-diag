using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class RateLimitsTests : IDisposable
{
    private readonly SessionHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string RateLine => Fixture.Line("simple-turn.jsonl", e => e is RateLimitUpdate);

    [Fact]
    public void Parser_FiveHourAndWeek()
    {
        // Строка из спайка: five_hour 7%, seven_day 57%, время сброса — unix-секунды.
        var r = Assert.IsType<RateLimitUpdate>(Assert.Single(StreamJsonParser.Parse(RateLine, DateTimeOffset.UnixEpoch)));
        Assert.Equal("allowed", r.Info.Status);
        Assert.Equal(0.07, r.Info.FiveHour!.Utilization, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790344800), r.Info.FiveHour.ResetsAt);
        Assert.Equal(0.57, r.Info.SevenDay!.Utilization, 3);
    }

    [Fact]
    public async Task Session_RecordsForItsProfile_NotInFeed()
    {
        // Лимиты — на аккаунт: у claude и claude2 они свои.
        var s = _h.Manager.Create("161432", "claude2");
        await s.SendAsync("привет");
        _h.Last.Emit(RateLine);

        Assert.Equal(0.57, _h.Limits.Get("claude2")!.Info.SevenDay!.Utilization, 3);
        Assert.Null(_h.Limits.Get("claude"));
        Assert.DoesNotContain(s.History, e => e is RateLimitUpdate);
    }

    [Fact]
    public void Ledger_SurvivesRestart()
    {
        var info = new RateLimitInfo("allowed", new RateLimitWindow(0.4, DateTimeOffset.UnixEpoch), null);
        _h.Limits.Update("claude2", info);
        var again = new LimitsLedger(Path.Combine(_h.Dir, "desk-limits.json"), TimeProvider.System);
        Assert.Equal(0.4, again.Get("claude2")!.Info.FiveHour!.Utilization, 3);
    }
}
