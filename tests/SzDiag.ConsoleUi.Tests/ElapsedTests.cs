using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class ElapsedTests
{
    [Fact]
    public void Format_Seconds() =>
        Assert.Equal("44сек", Elapsed.Format(TimeSpan.FromSeconds(44)));

    [Fact]
    public void Format_MinutesAndSeconds() =>
        Assert.Equal("5мин 44сек", Elapsed.Format(TimeSpan.FromSeconds(344)));

    [Fact]
    public void Format_HoursAndMinutes() =>
        Assert.Equal("1ч 05мин", Elapsed.Format(TimeSpan.FromMinutes(65)));

    [Fact]
    public void Format_NegativeClampsToZero() =>
        Assert.Equal("0сек", Elapsed.Format(TimeSpan.FromSeconds(-5)));
}
