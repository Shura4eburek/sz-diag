using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class AnsiTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void SetScrollRegion_EmitsDecstbm() =>
        Assert.Equal($"{Esc}[4;30r", Ansi.SetScrollRegion(4, 30));

    [Fact]
    public void ResetScrollRegion_EmitsBareR() =>
        Assert.Equal($"{Esc}[r", Ansi.ResetScrollRegion);

    [Fact]
    public void MoveCursor_IsOneBased() =>
        Assert.Equal($"{Esc}[1;1H", Ansi.MoveCursor(1, 1));

    [Fact]
    public void SaveRestore_UseDecScDecRc()
    {
        // DECSC/DECRC (ESC 7 / ESC 8) надёжнее SCO-варианта в conhost.
        Assert.Equal($"{Esc}7", Ansi.SaveCursor);
        Assert.Equal($"{Esc}8", Ansi.RestoreCursor);
    }

    [Fact]
    public void MarkupToAnsi_RendersColorAndKeepsText()
    {
        var s = Ansi.MarkupToAnsi("[green]online[/] дальше");
        Assert.Contains("online", s);
        Assert.Contains("дальше", s);
        Assert.Contains(Esc, s);           // цвет реально применён
        Assert.DoesNotContain("[green]", s); // разметка съедена, а не напечатана
        Assert.DoesNotContain("\n", s);    // панель рисуется построчно, переводов быть не должно
    }

    [Fact]
    public void MarkupToAnsi_DoesNotWrapLongLines()
    {
        var s = Ansi.MarkupToAnsi(new string('x', 500));
        Assert.DoesNotContain("\n", s);
    }
}
