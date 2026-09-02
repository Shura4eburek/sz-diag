namespace SzDiag.Cli.Tests;

/// <summary>
/// Коды возврата различают причины сбоя, потому что лечатся они по-разному: «сервис не
/// поднят», «нет логина», «захват занят» и «интерфейс не распознан» — четыре разных
/// действия человека.
/// </summary>
public class ErpExitCodeTests
{
    [Theory]
    [InlineData("unavailable", 3)]
    [InlineData("no_token", 3)]
    [InlineData("client_not_running", 4)]
    [InlineData("client_not_logged_in", 4)]
    [InlineData("busy", 5)]
    [InlineData("not_found", 6)]
    [InlineData("ambiguous", 6)]
    [InlineData("anchor_missing", 7)]
    [InlineData("timeout", 1)]
    [InlineData("internal", 1)]
    [InlineData("что-то новое", 1)]
    public void Код_апи_превращается_в_код_возврата(string apiCode, int expected)
        => Assert.Equal(expected, ErpCommand.ExitCodeFor(apiCode));

    [Fact]
    public void Команда_sz_числится_известной()
        => Assert.True(CliCommands.IsKnown("sz"));
}
