using Xunit;

namespace SzDiag.Hub.Tests;

public class ThreadPoolWatchdogTests
{
    [Theory]
    [InlineData(26, 300, false)]   // здоровый hub сразу после рестарта (бэклог п.50)
    [InlineData(300, 300, true)]
    [InlineData(3674, 300, true)]  // залипший hub с живой заявки 160306
    public void IsStarving_ComparesAgainstThreshold(int threadCount, int threshold, bool expected)
        => Assert.Equal(expected, ThreadPoolWatchdog.IsStarving(threadCount, threshold));
}
