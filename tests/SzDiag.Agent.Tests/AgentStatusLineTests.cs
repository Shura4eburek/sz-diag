using SzDiag.Agent;

namespace SzDiag.Agent.Tests;

public class AgentStatusLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static AgentStatusContext Ctx(
        DateTimeOffset? lastHeartbeat = null,
        DateTimeOffset? watchdogAt = null,
        DateTimeOffset? bootTime = null,
        string mode = "") => new(
        Sz: "156864",
        HubUrl: "http://192.168.1.10:5099",
        SshPort: 2222,
        WatchdogAt: watchdogAt ?? Now + TimeSpan.FromHours(3) + TimeSpan.FromMinutes(42),
        BootTime: bootTime ?? Now - TimeSpan.FromHours(5) - TimeSpan.FromMinutes(12),
        LastHeartbeatOk: lastHeartbeat ?? Now - TimeSpan.FromSeconds(5),
        HeartbeatTimeout: TimeSpan.FromSeconds(60),
        Mode: mode,
        Now: Now);

    private static string Plain(string markup) => SzDiag.ConsoleUi.MarkupText.Plain(markup);

    [Fact]
    public void Render_FirstLine_HasSzHubPortWatchdog()
    {
        var lines = AgentStatusLine.Render(Ctx(), width: 120);
        Assert.Equal(2, lines.Count);
        var first = Plain(lines[0]);
        Assert.Contains("СЗ 156864", first);
        Assert.Contains("192.168.1.10:5099", first);
        Assert.Contains("2222", first);
        Assert.Contains("3ч 42мин", first);
    }

    [Fact]
    public void Render_SecondLine_HasHotkeysAndUptime()
    {
        var second = Plain(AgentStatusLine.Render(Ctx(), width: 120)[1]);
        Assert.Contains("[C]", second);
        Assert.Contains("[Q]", second);
        Assert.Contains("5ч 12мин", second);
    }

    [Fact]
    public void Render_FreshHeartbeat_ShowsOnline() =>
        Assert.Contains("online", Plain(AgentStatusLine.Render(Ctx(), width: 120)[0]));

    [Fact]
    public void Render_StaleHeartbeat_ShowsReconnecting()
    {
        var lines = AgentStatusLine.Render(Ctx(lastHeartbeat: Now - TimeSpan.FromSeconds(90)), width: 120);
        var first = Plain(lines[0]);
        Assert.Contains("переподключение", first);
        Assert.DoesNotContain("online", first);
    }

    [Fact]
    public void Render_NoHeartbeatYet_ShowsConnecting()
    {
        var ctx = Ctx() with { LastHeartbeatOk = null };
        Assert.Contains("подключение", Plain(AgentStatusLine.Render(ctx, width: 120)[0]));
    }

    [Fact]
    public void Render_NoWatchdog_ShowsDash()
    {
        var ctx = Ctx() with { WatchdogAt = null };
        Assert.Contains("watchdog —", Plain(AgentStatusLine.Render(ctx, width: 120)[0]));
    }

    [Fact]
    public void Render_ExpiredWatchdog_ClampsToZero()
    {
        var ctx = Ctx(watchdogAt: Now - TimeSpan.FromMinutes(5));
        Assert.Contains("watchdog 0сек", Plain(AgentStatusLine.Render(ctx, width: 120)[0]));
    }

    [Fact]
    public void Render_Mode_IsShownWhenSet()
    {
        var first = Plain(AgentStatusLine.Render(Ctx(mode: "WinPE"), width: 120)[0]);
        Assert.Contains("WinPE", first);
    }

    [Fact]
    public void Render_NeverExceedsWidth()
    {
        foreach (var width in new[] { 40, 60, 80, 120, 200 })
        foreach (var line in AgentStatusLine.Render(Ctx(mode: "WinPE"), width))
            Assert.True(Plain(line).Length <= width,
                $"ширина {width}: строка длиной {Plain(line).Length}");
    }

    [Fact]
    public void Render_VeryNarrow_DoesNotThrow() =>
        Assert.Null(Record.Exception(() => AgentStatusLine.Render(Ctx(), width: 5)));
}
