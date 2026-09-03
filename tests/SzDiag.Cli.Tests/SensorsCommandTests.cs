using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>`sensors start` раньше молча перезатирал состояние прошлого прогона на хосте —
/// CSV не терялся (у каждого прогона свой файл с таймстампом), но CLI переставал видеть job
/// прошлого прогона и не мог его ни остановить, ни показать статус (бэклог п.145).</summary>
public class SensorsCommandTests
{
    [Fact]
    public void PreviousRunWarning_NoPreviousRun_ReturnsNull()
        => Assert.Null(SensorsCommand.PreviousRunWarning(null, null, null));

    [Fact]
    public void PreviousRunWarning_PreviousRunExists_MentionsJobAndCsv()
    {
        var warning = SensorsCommand.PreviousRunWarning(
            "job-1", @"C:\ProgramData\szdiag\sensors\156864-20260904-100000.csv",
            new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));

        Assert.NotNull(warning);
        Assert.Contains("job-1", warning);
        Assert.Contains("156864-20260904-100000.csv", warning);
    }
}
