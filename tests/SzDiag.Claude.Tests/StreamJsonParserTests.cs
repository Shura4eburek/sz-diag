using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class StreamJsonParserTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private const string SpikeSession = "8c8e3bf5-ea25-4879-a3dc-566095eba936";

    [Fact]
    public void SimpleTurn_InitTextResult()
    {
        var ev = Fixture.Events("simple-turn.jsonl");
        Assert.Equal(SpikeSession, Assert.Single(ev.OfType<SystemInit>()).SessionId);
        Assert.Equal("Привет", Assert.Single(ev.OfType<AssistantText>()).Text);
        var r = Assert.Single(ev.OfType<TurnResult>());
        Assert.False(r.IsError);
        Assert.False(r.Interrupted);
        Assert.Equal("Привет", r.Text);
        Assert.Equal(5, r.Usage.Output);
        Assert.True(r.CostUsd > 0);
        Assert.Contains(ev, e => e is ServiceEvent { Subtype: "hook_started" });
    }

    [Fact]
    public void ToolTurn_UseAndResultPaired()
    {
        var ev = Fixture.Events("tool-turn.jsonl");
        var use = Assert.Single(ev.OfType<ToolUse>());
        Assert.Equal("PowerShell", use.Name);
        Assert.Contains("szcli", use.Input.GetProperty("command").GetString());
        Assert.NotEqual(DateTimeOffset.UnixEpoch, use.At);   // время из поля timestamp, а не момент приёма
        var res = Assert.Single(ev.OfType<ToolResult>());
        Assert.Equal(use.Id, res.ToolUseId);
        Assert.False(res.IsError);
        Assert.False(res.Denied);
        Assert.Contains("szcli 1.0.0", res.Text);
    }

    [Fact]
    public void PermissionDeny_ResultMarkedDenied()
    {
        var res = Assert.Single(Fixture.Events("permission-deny-turn.jsonl").OfType<ToolResult>());
        Assert.True(res.IsError);
        Assert.True(res.Denied);
        Assert.Equal("отклонено", res.Text);
    }

    [Fact]
    public void PermissionAllow_ResultNotDenied()
    {
        var res = Assert.Single(Fixture.Events("permission-turn.jsonl").OfType<ToolResult>());
        Assert.False(res.Denied);
        Assert.Contains("File created successfully", res.Text);
    }

    [Fact]
    public void Interrupt_ControlResponseAndAbortedTurn_ThenNextTurn()
    {
        var ev = Fixture.Events("interrupt-turn.jsonl");
        var ctl = Assert.Single(ev.OfType<ControlResponse>());
        Assert.Equal("int-1", ctl.RequestId);
        Assert.True(ctl.Success);
        var results = ev.OfType<TurnResult>().ToList();
        Assert.Equal(2, results.Count);
        Assert.True(results[0].Interrupted);
        Assert.True(results[0].IsError);
        Assert.Equal("Жив.", results[1].Text);
        Assert.Equal(2, ev.OfType<SystemInit>().Count());   // init приходит на каждый ход (спайк)
    }

    [Fact]
    public void Resume_KeepsSessionId()
        => Assert.Equal(SpikeSession, Assert.Single(Fixture.Events("resume-init.jsonl").OfType<SystemInit>()).SessionId);

    [Fact]
    public void BrokenLine_ParseError()
        => Assert.IsType<ParseError>(Assert.Single(StreamJsonParser.Parse("{не json", T0)));

    [Fact]
    public void EmptyLine_Nothing() => Assert.Empty(StreamJsonParser.Parse("   ", T0));

    [Fact]
    public void UnknownType_KeptRaw()
    {
        var u = Assert.IsType<UnknownEvent>(Assert.Single(StreamJsonParser.Parse("""{"type":"new_thing","x":1}""", T0)));
        Assert.Equal("new_thing", u.Type);
        Assert.Contains("\"x\":1", u.Raw);
    }

    [Fact]
    public void Bom_Stripped()
        => Assert.IsType<ServiceEvent>(Assert.Single(StreamJsonParser.Parse("\uFEFF{\"type\":\"rate_limit_event\"}", T0)));

    [Fact]
    public void NoTimestamp_UsesReceivedAt()
        => Assert.Equal(T0, StreamJsonParser.Parse("""{"type":"rate_limit_event"}""", T0)[0].At);

    [Fact]
    public void DeskLines_RoundTrip()
    {
        var at = new DateTimeOffset(2026, 9, 25, 12, 1, 2, TimeSpan.Zero);

        var user = DeskLines.Serialize(new DeskUserMessage("проверь SMART") { At = at })!;
        var u = Assert.IsType<DeskUserMessage>(Assert.Single(StreamJsonParser.Parse(user, T0)));
        Assert.Equal("проверь SMART", u.Text);
        Assert.Equal(at, u.At);

        var crash = DeskLines.Serialize(new ProcessCrashed(1, new[] { "boom" }) { At = at })!;
        var c = Assert.IsType<ProcessCrashed>(Assert.Single(StreamJsonParser.Parse(crash, T0)));
        Assert.Equal(1, c.ExitCode);
        Assert.Equal("boom", Assert.Single(c.StderrTail));

        using var doc = JsonDocument.Parse("""{"command":"dir"}""");
        var asked = DeskLines.Serialize(new PermissionAsked("r1", "Bash", doc.RootElement.Clone(), "t1") { At = at })!;
        var a = Assert.IsType<PermissionAsked>(Assert.Single(StreamJsonParser.Parse(asked, T0)));
        Assert.Equal("dir", a.Input.GetProperty("command").GetString());
        Assert.Equal("t1", a.ToolUseId);

        var answered = DeskLines.Serialize(new PermissionAnswered("r1", true) { At = at })!;
        Assert.True(Assert.IsType<PermissionAnswered>(Assert.Single(StreamJsonParser.Parse(answered, T0))).Allowed);
    }

    [Fact]
    public void DeskLines_NotDeskEvent_Null() => Assert.Null(DeskLines.Serialize(new ServiceEvent("x", null)));
}
