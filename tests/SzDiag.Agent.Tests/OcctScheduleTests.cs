using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class OcctScheduleTests
{
    private const string ThreePeriods = """
        { "Periods": [
            { "TestType": "CpuOcct", "Duration": "00:45:00", "IsInfinite": false },
            { "TestType": "PowerSupply", "Duration": "00:30:00", "IsInfinite": false },
            { "TestType": "CpuLinpack", "Duration": "00:40:00", "IsInfinite": false }
        ] }
        """;

    [Fact]
    public void TryParsePeriods_ReadsTestTypeDurationAndInfiniteFlag()
    {
        var periods = OcctSchedule.TryParsePeriods(ThreePeriods);

        Assert.NotNull(periods);
        Assert.Equal(3, periods!.Count);
        Assert.Equal("CpuOcct", periods[0].TestType);
        Assert.Equal(TimeSpan.FromMinutes(45), periods[0].Duration);
        Assert.False(periods[0].IsInfinite);
    }

    [Fact]
    public void TryParsePeriods_ReturnsNull_WhenPeriodsIsNotAnArray()
    {
        // Регрессия (б.128, п.65): `$sched.Periods = foreach (...) {...}` с ОДНИМ периодом
        // сериализуется ConvertTo-Json объектом ({…}), а не массивом — OCCT на такой файл
        // отвечает "Could not load the schedule file", ложью про путь. Наш парсер обязан
        // явно отказаться, а не молча "разобрать" один период мимо структуры.
        var json = """{ "Periods": { "TestType": "Combined", "Duration": "00:05:00" } }""";

        Assert.Null(OcctSchedule.TryParsePeriods(json));
    }

    [Fact]
    public void TryParsePeriods_ReturnsNull_ForBrokenJson()
    {
        Assert.Null(OcctSchedule.TryParsePeriods("{ not json"));
    }

    [Fact]
    public void TryParsePeriods_ReturnsNull_WhenNoPeriodsProperty()
    {
        Assert.Null(OcctSchedule.TryParsePeriods("""{ "Foo": 1 }"""));
    }

    [Fact]
    public void TotalFiniteDuration_SumsOnlyFinitePeriods()
    {
        var periods = OcctSchedule.TryParsePeriods(ThreePeriods)!;

        Assert.Equal(TimeSpan.FromMinutes(115), OcctSchedule.TotalFiniteDuration(periods));
    }

    [Fact]
    public void TotalFiniteDuration_SkipsInfinitePeriod()
    {
        var json = """
            { "Periods": [
                { "TestType": "CpuOcct", "Duration": "00:10:00", "IsInfinite": false },
                { "TestType": "PowerSupply", "Duration": "00:00:00", "IsInfinite": true }
            ] }
            """;
        var periods = OcctSchedule.TryParsePeriods(json)!;

        Assert.True(OcctSchedule.HasInfinitePeriod(periods));
        Assert.Equal(TimeSpan.FromMinutes(10), OcctSchedule.TotalFiniteDuration(periods));
    }

    [Fact]
    public void PeriodsNotReached_ListsPeriodsAfterBudgetIsSpent()
    {
        // Регрессия (СЗ 161346): 45+30+40 мин при таймауте 75 мин — CpuLinpack (третий
        // период) не стартует вовсе, а не "тест затянулся на 5 минут".
        var periods = OcctSchedule.TryParsePeriods(ThreePeriods)!;

        var missed = OcctSchedule.PeriodsNotReached(periods, budgetSeconds: 75 * 60);

        Assert.Equal(new[] { "CpuLinpack" }, missed);
    }

    [Fact]
    public void PeriodsNotReached_EmptyWhenScheduleFitsBudget()
    {
        var periods = OcctSchedule.TryParsePeriods(ThreePeriods)!;

        var missed = OcctSchedule.PeriodsNotReached(periods, budgetSeconds: 200 * 60);

        Assert.Empty(missed);
    }
}
