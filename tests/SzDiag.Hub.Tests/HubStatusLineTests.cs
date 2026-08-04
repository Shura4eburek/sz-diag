using SzDiag.Contracts;
using SzDiag.Hub;

namespace SzDiag.Hub.Tests;

public class HubStatusLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static SessionInfo Session(string sz, SessionStatus status = SessionStatus.Online,
        string? activity = null, DateTimeOffset? activitySince = null) =>
        new(sz, "192.168.1.50", "PC-" + sz, status, Now, Now,
            Activity: activity ?? "", ActivitySince: activitySince);

    private static HubStatusContext Ctx(IReadOnlyList<SessionInfo> sessions) => new(
        Sessions: sessions,
        ListenUrls: "0.0.0.0:5099",
        LanIp: "192.168.1.10",
        KbRoot: @"C:\Users\ENDI\kb",
        StartedAt: Now - TimeSpan.FromHours(5) - TimeSpan.FromMinutes(12),
        Now: Now);

    /// <summary>Убирает Spectre-разметку — тесты проверяют текст, а не цвета.</summary>
    private static string Plain(string markup) => SzDiag.ConsoleUi.MarkupText.Plain(markup);

    [Fact]
    public void Render_ShowsListenAddressLanIpAndUptime()
    {
        var lines = HubStatusLine.Render(Ctx(Array.Empty<SessionInfo>()), width: 120);
        Assert.Equal(2, lines.Count);
        var first = Plain(lines[0]);
        Assert.Contains("0.0.0.0:5099", first);
        Assert.Contains("192.168.1.10", first);
        Assert.Contains("5ч 12мин", first);
    }

    [Fact]
    public void Render_NoSessions_SaysSo()
    {
        var lines = HubStatusLine.Render(Ctx(Array.Empty<SessionInfo>()), width: 120);
        Assert.Contains("нет активных СЗ", Plain(lines[1]));
    }

    [Fact]
    public void Render_ListsOnlineSessionsWithCount()
    {
        var lines = HubStatusLine.Render(
            Ctx(new[] { Session("156864"), Session("160176") }), width: 120);
        var second = Plain(lines[1]);
        Assert.Contains("онлайн 2:", second);
        Assert.Contains("156864", second);
        Assert.Contains("160176", second);
    }

    [Fact]
    public void Render_SkipsOfflineSessions()
    {
        var lines = HubStatusLine.Render(
            Ctx(new[] { Session("156864"), Session("999999", SessionStatus.Offline) }), width: 120);
        var second = Plain(lines[1]);
        Assert.Contains("онлайн 1:", second);
        Assert.DoesNotContain("999999", second);
    }

    [Fact]
    public void Render_ShowsActivityWithElapsed()
    {
        var lines = HubStatusLine.Render(
            Ctx(new[] { Session("156864", activity: "OCCT", activitySince: Now - TimeSpan.FromMinutes(42)) }),
            width: 120);
        Assert.Contains("156864 (OCCT 42мин 00сек)", Plain(lines[1]));
    }

    [Fact]
    public void Render_TruncatesSessionListToWidth_WithPlusN()
    {
        var many = Enumerable.Range(0, 12).Select(i => Session($"16000{i}")).ToArray();
        var lines = HubStatusLine.Render(Ctx(many), width: 60);
        var second = Plain(lines[1]);
        Assert.True(second.Length <= 60, $"строка длиннее ширины: {second.Length}");
        Assert.Contains("+", second);
    }

    [Fact]
    public void Render_DropsKbTail_WhenNarrow()
    {
        var wide = Plain(HubStatusLine.Render(Ctx(new[] { Session("156864") }), width: 200)[1]);
        var narrow = Plain(HubStatusLine.Render(Ctx(new[] { Session("156864") }), width: 30)[1]);
        Assert.Contains("kb", wide);
        Assert.DoesNotContain("kb", narrow);
    }

    [Fact]
    public void Render_NeverExceedsWidth()
    {
        var many = Enumerable.Range(0, 30).Select(i => Session($"1600{i:D2}",
            activity: "OCCT", activitySince: Now - TimeSpan.FromMinutes(5))).ToArray();
        foreach (var width in new[] { 40, 60, 80, 120, 200 })
        {
            var lines = HubStatusLine.Render(Ctx(many), width);
            foreach (var line in lines)
                Assert.True(Plain(line).Length <= width,
                    $"ширина {width}: строка длиной {Plain(line).Length}");
        }
    }

    [Fact]
    public void Render_VeryNarrow_DoesNotThrow()
    {
        var ex = Record.Exception(() => HubStatusLine.Render(Ctx(new[] { Session("156864") }), width: 5));
        Assert.Null(ex);
    }
}
