using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Калькулятор окна прогона по исторической частоте отказов (бэклог п.45, СЗ 160587:
/// 18 минут per-core прогона при интервале ~11 минут дали мощность ~25 % — "+0 WHEA" по
/// такому окну не является отрицательным результатом, но выглядит как он).</summary>
public class WindowCalculatorTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 4, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MedianIntervalMinutes_RegularEvents_ReturnsGap()
    {
        var times = new[] { Base, Base.AddMinutes(11), Base.AddMinutes(22), Base.AddMinutes(33) };
        Assert.Equal(11, WindowCalculator.MedianIntervalMinutes(times));
    }

    [Fact]
    public void MedianIntervalMinutes_FewerThanTwoEvents_ReturnsNull()
    {
        Assert.Null(WindowCalculator.MedianIntervalMinutes(Array.Empty<DateTimeOffset>()));
        Assert.Null(WindowCalculator.MedianIntervalMinutes(new[] { Base }));
    }

    [Fact]
    public void MedianIntervalMinutes_IgnoresOneLongIdleGap()
    {
        // Регулярные события раз в 11 минут вечером + недельный простой между заявками не
        // должны раздувать "средний интервал" до недели - медиана устойчива к таким выбросам.
        var times = new[]
        {
            Base, Base.AddMinutes(11), Base.AddMinutes(22),
            Base.AddDays(7), Base.AddDays(7).AddMinutes(11),
        };
        Assert.Equal(11, WindowCalculator.MedianIntervalMinutes(times));
    }

    [Fact]
    public void RequiredMinutesFor_95Percent_IsAboutThreeTimesMeanInterval()
    {
        var required = WindowCalculator.RequiredMinutesFor(11, 0.95);
        Assert.InRange(required, 32, 34);   // ~35 мин из формулировки боли (округление)
    }

    [Fact]
    public void Power_ShortWindow_MatchesLowCapturedProbability()
    {
        // Пример из боли: интервал ~11 минут, запрошено 3 минуты -> мощность ~24-26 %.
        var power = WindowCalculator.Power(3, 11);
        Assert.InRange(power, 0.20, 0.30);
    }

    [Fact]
    public void Power_AtRequiredDuration_ReachesConfidence()
    {
        var required = WindowCalculator.RequiredMinutesFor(11, 0.95);
        var power = WindowCalculator.Power(required, 11);
        Assert.InRange(power, 0.94, 0.96);
    }

    [Fact]
    public void Build_TooShortRequest_IsFlagged()
    {
        var times = new[] { Base, Base.AddMinutes(11), Base.AddMinutes(22) };
        var plan = WindowCalculator.Build(times, requestedMinutes: 3);

        Assert.NotNull(plan);
        Assert.True(plan!.TooShort);
        Assert.InRange(plan.Power, 0.20, 0.30);
    }

    [Fact]
    public void Build_SufficientRequest_IsNotFlagged()
    {
        var times = new[] { Base, Base.AddMinutes(11), Base.AddMinutes(22) };
        var plan = WindowCalculator.Build(times, requestedMinutes: 40);

        Assert.NotNull(plan);
        Assert.False(plan!.TooShort);
    }

    [Fact]
    public void Build_NoHistory_ReturnsNull()
        => Assert.Null(WindowCalculator.Build(Array.Empty<DateTimeOffset>(), 10));
}
