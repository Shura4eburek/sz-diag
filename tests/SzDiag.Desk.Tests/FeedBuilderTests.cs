using System.Text.Json;
using SzDiag.Claude;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class FeedBuilderTests
{
    private readonly List<(string Id, bool Allow)> _answers = new();
    private int _restarts;

    private FeedBuilder New() => new((id, allow) => _answers.Add((id, allow)), () => _restarts++);

    private FeedBuilder From(string fixture)
    {
        var f = New();
        foreach (var e in Fixture.Events(fixture)) f.Add(e);
        return f;
    }

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void SimpleTurn_OnlyAssistantText()
        => Assert.Equal("Привет", Assert.IsType<AssistantFeedItem>(Assert.Single(From("simple-turn.jsonl").Items)).Text);

    [Fact]
    public void ToolTurn_CardOk_WithSummaryOutputDuration()
    {
        var f = From("tool-turn.jsonl");
        var card = Assert.Single(f.Items.OfType<ToolFeedItem>());
        Assert.Equal(ToolStatus.Ok, card.Status);
        Assert.Contains("szcli", card.Summary);
        Assert.Contains("szcli 1.0.0", card.Output);
        Assert.StartsWith("✓ PowerShell · ", card.Header);
        Assert.NotNull(card.Duration);
        Assert.IsType<AssistantFeedItem>(f.Items[^1]);
    }

    [Fact]
    public void DenyTurn_CardDenied()
        => Assert.StartsWith("⊘ Write", Assert.Single(From("permission-deny-turn.jsonl").Items.OfType<ToolFeedItem>()).Header);

    [Fact]
    public void InterruptTurn_NoteThenNextAnswer()
    {
        var f = From("interrupt-turn.jsonl");
        Assert.Contains(f.Items, i => i is NoteFeedItem { Text: "ход прерван" });
        Assert.Equal("Жив.", Assert.IsType<AssistantFeedItem>(f.Items[^1]).Text);
    }

    [Fact]
    public void Permission_AskAnswer()
    {
        var f = New();
        f.Add(new PermissionAsked("r1", "Bash", J("""{"command":"dir C:\\"}"""), "t1"));
        var card = Assert.IsType<PermissionFeedItem>(Assert.Single(f.Items));
        Assert.True(card.IsPending);
        Assert.Equal("Разрешить Bash?", card.Title);
        Assert.Equal("dir C:\\", card.Summary);

        card.AllowCommand.Execute(null);
        Assert.Equal(("r1", true), Assert.Single(_answers));

        f.Add(new PermissionAnswered("r1", true));
        Assert.False(card.IsPending);
        Assert.Equal("разрешено", card.ResultText);
    }

    [Fact]
    public void StalePermission_Expired()
    {
        var f = New();
        f.Add(new PermissionAsked("old", "Bash", J("{}"), null));
        f.Add(new PermissionAsked("live", "Bash", J("{}"), null));
        f.ExpirePermissionsExcept(new[] { "live" });

        var cards = f.Items.OfType<PermissionFeedItem>().ToList();
        Assert.False(cards[0].IsPending);
        Assert.Contains("истёк", cards[0].ResultText);
        Assert.True(cards[1].IsPending);
    }

    [Fact]
    public void Crash_CardWithRestart()
    {
        var f = New();
        f.Add(new ProcessCrashed(2, new[] { "a", "b" }));
        var card = Assert.IsType<CrashFeedItem>(Assert.Single(f.Items));
        Assert.Equal("claude завершился (код 2)", card.Title);
        Assert.Equal("a\nb", card.Details);
        card.RestartCommand.Execute(null);
        Assert.Equal(1, _restarts);

        f.Add(new ProcessCrashed(null, new[] { "claude не найден" }));
        Assert.Equal("claude не запустился", Assert.IsType<CrashFeedItem>(f.Items[^1]).Title);
    }

    [Fact]
    public void Unknown_RawCard_ParseErrorSkipped()
    {
        var f = New();
        f.Add(new UnknownEvent("new_thing", "{}"));
        f.Add(new ParseError("{", "битая"));
        Assert.Equal("raw · new_thing", Assert.IsType<RawFeedItem>(Assert.Single(f.Items)).Title);
    }

    [Fact]
    public void HugeOutput_Clipped()
    {
        var f = New();
        f.Add(new ToolUse("m", "t1", "Bash", J("""{"command":"type big.log"}"""), null));
        f.Add(new ToolResult("t1", new string('x', 50_000), false, false, null));
        var card = Assert.Single(f.Items.OfType<ToolFeedItem>());
        Assert.StartsWith(new string('x', FeedBuilder.MaxOutputChars), card.Output);
        Assert.EndsWith("[вывод обрезан: показано 20000 из 50000 символов]", card.Output);
    }

    [Fact]
    public void SubagentEvents_CountedOnParentCard()
    {
        var f = New();
        f.Add(new ToolUse("m", "p1", "Task", J("""{"description":"разбор дампа"}"""), null));
        f.Add(new ToolUse("m2", "c1", "Bash", J("{}"), "p1"));
        f.Add(new ToolUse("m2", "c2", "Read", J("{}"), "p1"));
        f.Add(new AssistantText("m3", "промежуточное", "p1"));
        f.Add(new ToolResult("c1", "ok", false, false, "p1"));

        var card = Assert.IsType<ToolFeedItem>(Assert.Single(f.Items));
        Assert.Equal(2, card.SubSteps);
        Assert.EndsWith("· субагент: 2", card.Header);
    }

    [Theory]
    [InlineData("Bash", """{"command":"dir C:\\"}""", "dir C:\\")]
    [InlineData("Read", """{"file_path":"C:\\a.txt"}""", "C:\\a.txt")]
    [InlineData("mcp__x__y", """{"n":1,"q":"abc"}""", "abc")]
    [InlineData("PowerShell", """{"command":"Get-Date\nGet-Item x"}""", "Get-Date …")]
    [InlineData("Weird", "{}", "")]
    public void ToolSummary_PicksMeaningfulField(string tool, string input, string expected)
        => Assert.Equal(expected, ToolSummary.For(tool, J(input)));

    [Fact]
    public void ToolSummary_LongClipped()
    {
        var s = ToolSummary.For("Bash", J($$"""{"command":"{{new string('a', 300)}}"}"""));
        Assert.Equal(ToolSummary.Max, s.Length);
        Assert.EndsWith("…", s);
    }
}
