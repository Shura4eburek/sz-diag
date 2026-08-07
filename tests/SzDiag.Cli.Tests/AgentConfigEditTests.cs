using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>У клиента стоял `WatchdogHours: 1`, и поднять его на работающей машине было
/// нельзя: агент читает конфиг при старте, а перезапустить его удалённо тогда было нечем
/// (бэклог п.86).</summary>
public class AgentConfigEditTests
{
    [Fact]
    public void ParseAssignment_TakesKeyAndValue()
    {
        var parsed = AgentConfigEdit.ParseAssignment("WatchdogHours=12");

        Assert.Equal("WatchdogHours", parsed!.Value.Key);
        Assert.Equal("12", parsed.Value.Value);
    }

    [Theory]
    [InlineData("WatchdogHours")]
    [InlineData("=12")]
    [InlineData("")]
    public void ParseAssignment_Garbage_IsRejected(string text)
        => Assert.Null(AgentConfigEdit.ParseAssignment(text));

    [Fact]
    public void KnownKeys_CoverHotAndRestartOnes()
    {
        Assert.True(AgentConfigEdit.IsKnownKey("watchdoghours"));   // регистр не важен
        Assert.True(AgentConfigEdit.IsKnownKey("SshPort"));
        Assert.False(AgentConfigEdit.IsKnownKey("WatchdogHoursTypo"));
    }

    [Fact]
    public void Script_ForWatchdogHours_RearmsTheTask()
    {
        // Без перевзвода задачи правка файла ничего не меняет: срок уже зафиксирован в -Once.
        var script = AgentConfigEdit.BuildScript("160306", "WatchdogHours", "12");

        Assert.Contains("szdiag-watchdog-160306", script);
        Assert.Contains("Register-ScheduledTask", script);
        Assert.Contains("AddHours", script);
    }

    [Fact]
    public void Script_ForOtherKeys_SaysWhenItWillApply()
    {
        var script = AgentConfigEdit.BuildScript("160306", "SshPort", "2222");

        Assert.Contains("вступит в силу при следующем открытии", script);
    }

    [Fact]
    public void Script_QuotesStringsAndLeavesNumbersBare()
    {
        Assert.Contains("-NotePropertyValue 12", AgentConfigEdit.BuildScript("160306", "WatchdogHours", "12"));
        Assert.Contains("-NotePropertyValue 'svc-diag2'",
            AgentConfigEdit.BuildScript("160306", "ServiceAccount", "svc-diag2"));
    }

    [Fact]
    public void Script_FindsConfigNextToLiveAgent()
    {
        var script = AgentConfigEdit.BuildScript("160306", "HeartbeatSeconds", "30");

        Assert.Contains("Get-Process -Name 'SzDiag.Agent'", script);
        Assert.Contains("appsettings.json", script);
    }
}
