using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Транспортное представление расписания OCCT для `GET /api/occt/schedule`
/// (бэклог п.124/#60) — план прогона, который `test run` печатает ДО старта.</summary>
public class OcctSchedulePlanTests
{
    [Fact]
    public void From_TwoFinitePeriods_SumsTotalAndKeepsOrder()
    {
        var periods = new[]
        {
            new OcctSchedule.Period("Combined", TimeSpan.FromMinutes(90), false),
            new OcctSchedule.Period("PowerSupply", TimeSpan.FromMinutes(90), false),
        };

        var plan = OcctSchedulePlan.From(periods);

        Assert.Equal(2, plan.Periods.Count);
        Assert.Equal("Combined", plan.Periods[0].TestType);
        Assert.Equal(TimeSpan.FromMinutes(180), plan.TotalFinite);
        Assert.False(plan.HasInfinite);
    }

    [Fact]
    public void From_InfinitePeriod_FlagsHasInfinite_ExcludedFromTotal()
    {
        var periods = new[]
        {
            new OcctSchedule.Period("PowerSupply", TimeSpan.Zero, true),
            new OcctSchedule.Period("CpuLinpack", TimeSpan.FromMinutes(40), false),
        };

        var plan = OcctSchedulePlan.From(periods);

        Assert.True(plan.HasInfinite);
        Assert.Equal(TimeSpan.FromMinutes(40), plan.TotalFinite);
    }

    [Fact]
    public void PeriodDto_DurationRoundTripsThroughSeconds()
    {
        var dto = new OcctSchedulePeriodDto("Combined", 5400, false);

        Assert.Equal(TimeSpan.FromMinutes(90), dto.Duration);
    }
}
