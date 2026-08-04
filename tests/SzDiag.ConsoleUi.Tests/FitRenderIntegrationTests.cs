using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class FitRenderIntegrationTests
{
    /// <summary>Fit режет по видимой ширине и может оставить тег незакрытым
    /// («[green]abcd» без [/]). Панель сразу отдаёт результат в MarkupToAnsi —
    /// проверяем, что Spectre это переваривает, а не кидает исключение.</summary>
    [Fact]
    public void MarkupToAnsi_AcceptsUnclosedTagLeftByFit()
    {
        var fitted = MarkupText.Fit("[green]abcdefghij[/] хвост", 4);
        var ex = Record.Exception(() => Ansi.MarkupToAnsi(fitted));
        Assert.Null(ex);
    }
}
