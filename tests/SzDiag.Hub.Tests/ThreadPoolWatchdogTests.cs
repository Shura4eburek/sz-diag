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

    /// <summary>19.10: отмена при неудачном старте hub уходила в crit «BackgroundService has thrown».</summary>
    [Fact]
    public async Task NextTick_Cancelled_EndsLoopWithoutException()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();
        var tick = ThreadPoolWatchdog.NextTickAsync(timer, cts.Token);
        cts.Cancel();
        Assert.False(await tick);
    }
}
