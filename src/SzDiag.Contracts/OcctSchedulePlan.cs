namespace SzDiag.Contracts;

/// <summary>Один период расписания OCCT — версия <see cref="OcctSchedule.Period"/> для передачи
/// по HTTP (`GET /api/occt/schedule`, бэклог п.124/#60): секунды, а не <c>TimeSpan</c>, так как
/// System.Text.Json не сериализует <c>TimeSpan</c> без кастомного конвертера.</summary>
public sealed record OcctSchedulePeriodDto(string TestType, double DurationSeconds, bool IsInfinite)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
}

/// <summary>План прогона OCCT — то, что `szcli test run` печатает ДО старта (бэклог п.124/#60,
/// СЗ 161346): «Combined 90 мин + PowerSupply 90 мин, итого 3:00». Строится из расписания,
/// реально лежащего в раздаче (<c>Hub.ToolsRoot/occt/…</c>), а не из репозитория — раздача
/// молча расходилась с репо (5+5 минут вместо заявленных 90+90).</summary>
public sealed record OcctSchedulePlan(IReadOnlyList<OcctSchedulePeriodDto> Periods,
    double TotalFiniteSeconds, bool HasInfinite)
{
    public TimeSpan TotalFinite => TimeSpan.FromSeconds(TotalFiniteSeconds);

    public static OcctSchedulePlan From(IReadOnlyList<OcctSchedule.Period> periods) => new(
        periods.Select(p => new OcctSchedulePeriodDto(p.TestType, p.Duration.TotalSeconds, p.IsInfinite)).ToList(),
        OcctSchedule.TotalFiniteDuration(periods).TotalSeconds,
        OcctSchedule.HasInfinitePeriod(periods));
}
