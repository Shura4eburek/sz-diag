namespace SzDiag.Cli;

/// <summary>Калькулятор окна прогона по исторической частоте отказов (бэклог п.45, СЗ 160587).
///
/// Прицельный per-core прогон по 3 минуты на ядро (18 минут суммарно) дал «+0 WHEA на всех
/// ядрах» — и это выглядело как отрицательный результат, хотя таковым не было: в самый плотный
/// вечер события шли раз в ~11 минут, а окно в 3 минуты давало шанс поймать событие ~25 %.
/// «Ничего не выбило» по такому окну нельзя было отличить от «ядро чистое» — не хватало самой
/// величины «мощность теста».
///
/// Модель — простейшая (пуассоновский поток отказов, интервалы между ними экспоненциальны):
/// вероятность поймать хотя бы одно событие за время T при среднем интервале I равна
/// 1 - exp(-T/I). Отсюда T для заданной уверенности c: T = -I * ln(1-c) (при c=0.95 это
/// T ≈ 3·I — то самое приближение «95 % ≈ 3× средний интервал» из формулировки боли).</summary>
public static class WindowCalculator
{
    /// <summary>Результат расчёта: сколько нужно времени для заданной уверенности и какая
    /// мощность у уже запрошенной длительности.</summary>
    public sealed record Plan(
        double MeanIntervalMinutes,
        double RequestedMinutes,
        double RequiredMinutes,
        double Power)
    {
        /// <summary>Запрошенное окно заведомо коротко — тест не даёт права на вывод
        /// «дефект не воспроизвёлся», даже если ничего не поймано.</summary>
        public bool TooShort => RequestedMinutes < RequiredMinutes;
    }

    /// <summary>Медиана интервалов между последовательными отказами — устойчивее среднего к
    /// редким долгим простоям между заявками (машина неделю выключена ≠ «событие раз в
    /// неделю»), поэтому не завышает нужное окно тем, чем завышал бы обычный mean.</summary>
    public static double? MedianIntervalMinutes(IReadOnlyList<DateTimeOffset> failureTimes)
    {
        if (failureTimes.Count < 2) return null;

        var sorted = failureTimes.OrderBy(t => t).ToList();
        var gaps = new List<double>();
        for (var i = 1; i < sorted.Count; i++)
            gaps.Add((sorted[i] - sorted[i - 1]).TotalMinutes);

        gaps.Sort();
        var mid = gaps.Count / 2;
        return gaps.Count % 2 == 1 ? gaps[mid] : (gaps[mid - 1] + gaps[mid]) / 2.0;
    }

    /// <summary>Требуемая длительность для вероятности поимки <paramref name="confidence"/>
    /// (по умолчанию 95 %) при среднем интервале между отказами <paramref name="meanIntervalMinutes"/>.</summary>
    public static double RequiredMinutesFor(double meanIntervalMinutes, double confidence = 0.95)
        => -meanIntervalMinutes * Math.Log(1 - confidence);

    /// <summary>Мощность теста — вероятность поймать хотя бы одно событие за
    /// <paramref name="requestedMinutes"/> при данном среднем интервале.</summary>
    public static double Power(double requestedMinutes, double meanIntervalMinutes)
        => meanIntervalMinutes <= 0 ? 1.0 : 1 - Math.Exp(-requestedMinutes / meanIntervalMinutes);

    /// <summary>Собирает план целиком по истории отказов и запрошенной длительности.</summary>
    public static Plan? Build(IReadOnlyList<DateTimeOffset> failureTimes, double requestedMinutes,
        double confidence = 0.95)
    {
        var mean = MedianIntervalMinutes(failureTimes);
        if (mean is null) return null;

        return new Plan(mean.Value, requestedMinutes, RequiredMinutesFor(mean.Value, confidence),
            Power(requestedMinutes, mean.Value));
    }
}
