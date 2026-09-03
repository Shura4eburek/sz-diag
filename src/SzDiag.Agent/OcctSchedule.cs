using System.Text.Json;

namespace SzDiag.Agent;

/// <summary>
/// Разбор расписания OCCT (<c>schedule.json</c>, поле <c>Periods[].TestType/Duration/IsInfinite</c>) —
/// нужен, чтобы поймать рассинхрон между длиной расписания и <c>durationSeconds</c> шага
/// ДО старта теста, а не через час простоя под нагрузкой. <c>Duration</c> — строка TimeSpan
/// ("00:45:00"), формат подтверждён рецептом <c>tools/recipes/client/set-occt-schedule.ps1</c>
/// (там же <c>[TimeSpan]::Parse($p.Duration)</c>).
///
/// Боль (бэклог п.129, СЗ 161346): расписание CPU было на 1 ч 55 мин (45+30+40), а
/// <c>durationSeconds</c> шага <c>occt</c> в <c>testsuite.json</c> — 4500 с (75 мин). Ровно
/// на 75-й минуте раннер убил процесс — третий период (CpuLinpack) не стартовал вовсе, а
/// в отчёте это выглядело как «тест затянулся», хотя фактически прогон не выполнен на треть.
/// </summary>
public static class OcctSchedule
{
    public sealed record Period(string TestType, TimeSpan Duration, bool IsInfinite);

    /// <summary>null, если это не расписание OCCT (нет массива <c>Periods</c>) или JSON битый —
    /// молчаливое отсутствие проверки лучше, чем ложный отказ на незнакомом формате.</summary>
    public static IReadOnlyList<Period>? TryParsePeriods(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Periods", out var periods) ||
                periods.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<Period>();
            foreach (var p in periods.EnumerateArray())
            {
                var testType = p.TryGetProperty("TestType", out var tt) ? (tt.GetString() ?? "?") : "?";
                var isInfinite = p.TryGetProperty("IsInfinite", out var inf) &&
                                  inf.ValueKind == JsonValueKind.True;
                var duration = TimeSpan.Zero;
                if (p.TryGetProperty("Duration", out var d) && d.ValueKind == JsonValueKind.String)
                    TimeSpan.TryParse(d.GetString(), out duration);
                list.Add(new Period(testType, duration, isInfinite));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Сумма КОНЕЧНЫХ периодов. Бесконечный период (<c>IsInfinite</c>) в сумму не
    /// входит — он по определению не укладывается ни в какой бюджет, его останавливает только
    /// <c>durationSeconds</c> шага, и это штатное поведение (напр. PowerSupply-транзиенты).</summary>
    public static TimeSpan TotalFiniteDuration(IReadOnlyList<Period> periods)
    {
        var total = TimeSpan.Zero;
        foreach (var p in periods)
            if (!p.IsInfinite) total += p.Duration;
        return total;
    }

    public static bool HasInfinitePeriod(IReadOnlyList<Period> periods) => periods.Any(p => p.IsInfinite);

    /// <summary>Какие периоды не успеют стартовать за <paramref name="budgetSeconds"/> — для
    /// сообщения «прогон неполный». Период недостижим, если сумма длительностей ДО него уже
    /// покрыла бюджет.</summary>
    public static IReadOnlyList<string> PeriodsNotReached(IReadOnlyList<Period> periods, int budgetSeconds)
    {
        var result = new List<string>();
        var elapsedSeconds = 0.0;
        var unbounded = false;
        foreach (var p in periods)
        {
            if (unbounded || elapsedSeconds >= budgetSeconds) result.Add(p.TestType);
            if (p.IsInfinite) unbounded = true;
            else elapsedSeconds += p.Duration.TotalSeconds;
        }
        return result;
    }
}
