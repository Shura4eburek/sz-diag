using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class MarkupTextTests
{
    [Fact]
    public void Plain_StripsTags() =>
        Assert.Equal("online дальше", MarkupText.Plain("[green]online[/] дальше"));

    [Fact]
    public void Plain_UnescapesDoubleBrackets() =>
        Assert.Equal("[C] закрыть", MarkupText.Plain("[green][[C]][/] закрыть"));

    [Fact]
    public void Plain_HandlesBareText() =>
        Assert.Equal("просто текст", MarkupText.Plain("просто текст"));

    [Fact]
    public void PlainLength_CountsVisibleOnly() =>
        Assert.Equal(6, MarkupText.PlainLength("[green]online[/]"));

    [Fact]
    public void PlainLength_CountsEscapedBracketsAsOne() =>
        Assert.Equal(3, MarkupText.PlainLength("[green][[C]][/]"));

    [Fact]
    public void Fit_ShorterThanWidth_ReturnsUnchanged()
    {
        const string s = "[green]online[/]";
        Assert.Equal(s, MarkupText.Fit(s, 20));
    }

    [Fact]
    public void Fit_TrimsVisibleTextToWidth()
    {
        var fitted = MarkupText.Fit("[green]abcdefghij[/]", 4);
        Assert.Equal(4, MarkupText.PlainLength(fitted));
        Assert.Equal("abcd", MarkupText.Plain(fitted));
    }

    [Fact]
    public void Fit_KeepsEscapedBracketsIntact()
    {
        var fitted = MarkupText.Fit("[green][[C]][/] закрыть СЗ", 3);
        Assert.Equal("[C]", MarkupText.Plain(fitted));
    }

    [Fact]
    public void Fit_ZeroWidth_ReturnsEmpty() =>
        Assert.Equal("", MarkupText.Fit("[green]online[/]", 0));

    [Fact]
    public void Fit_ClosesTagsLeftOpenByTrim()
    {
        // Spectre бросает исключение на незакрытом теге, поэтому обрезка обязана дозакрыть.
        Assert.Equal("[green]abcd[/]", MarkupText.Fit("[green]abcdefghij[/]", 4));
    }

    [Fact]
    public void Fit_ClosesNestedTags()
    {
        var fitted = MarkupText.Fit("[green]aa[bold]bbbb[/][/]", 3);
        Assert.Equal("aab", MarkupText.Plain(fitted));
        Assert.EndsWith("[/][/]", fitted);
    }
}
