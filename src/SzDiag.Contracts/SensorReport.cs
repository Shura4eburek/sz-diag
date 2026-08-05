using System.Globalization;

namespace SzDiag.Contracts;

/// <summary>Одна строка наблюдателя.</summary>
public sealed record SensorSample(
    DateTime Time,
    double? CpuPercent,
    int StressProcesses,
    double? CpuTempC,
    double? RamUsedPercent);

/// <summary>Разбор CSV наблюдателя и ответ на главный вопрос прогона: **сколько времени
/// нагрузка реально держалась**.
///
/// На 160306 прогон выглядел как «40 минут выстояла», а по CSV оказалось: комбинированная
/// нагрузка шла **4.2 минуты**, остальные 23 минуты машина простаивала — OCCT отработал
/// конечное расписание и вышел. Без этого пересчёта вывод по заявке был бы неверным
/// (бэклог п.7).</summary>
/// <param name="Samples">Сколько строк прочитано.</param>
/// <param name="LoadedMinutes">Сколько минут CPU был под нагрузкой (≥ порога).</param>
/// <param name="SpanMinutes">Сколько минут длился весь ряд наблюдений.</param>
/// <param name="LastSample">Последняя секунда, за которую есть данные, — при жёстком
/// вырубоне это и есть момент отказа.</param>
/// <param name="GapSeconds">Самый длинный разрыв в ряду: под 100 % нагрузкой наблюдатель
/// притормаживает, и это надо видеть, а не считать пропуск «спокойным периодом».</param>
public sealed record SensorSummary(
    int Samples,
    double LoadedMinutes,
    double SpanMinutes,
    DateTime? FirstSample,
    DateTime? LastSample,
    double? MaxCpu,
    double? AvgCpuUnderLoad,
    double? MaxTempC,
    double? MaxRamPercent,
    int MaxStressProcesses,
    double GapSeconds)
{
    /// <summary>Доля времени под нагрузкой — «тест реально грузил железо» в одну цифру.</summary>
    public double LoadedShare => SpanMinutes > 0 ? LoadedMinutes / SpanMinutes : 0;
}

public static class SensorReport
{
    /// <summary>Порог, с которого считаем, что нагрузка идёт. 60 % — с запасом ниже 90 %,
    /// чтобы не потерять прогоны с частично загруженным CPU (memory-тесты).</summary>
    public const double LoadThreshold = 60;

    /// <summary>Разбирает CSV наблюдателя (см. <see cref="SensorWatcher"/>).</summary>
    public static IReadOnlyList<SensorSample> Parse(string csv)
    {
        var samples = new List<SensorSample>();
        foreach (var raw in (csv ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("time;", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split(';');
            if (parts.Length < 3) continue;
            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var time)) continue;

            samples.Add(new SensorSample(
                time,
                Num(parts.ElementAtOrDefault(1)),
                (int)(Num(parts.ElementAtOrDefault(2)) ?? 0),
                Num(parts.ElementAtOrDefault(3)),
                Num(parts.ElementAtOrDefault(4))));
        }
        return samples;
    }

    /// <summary>Число из CSV: пустая ячейка (датчика нет) — это null, а не ноль. Ноль вместо
    /// «данных нет» читается как «холодный CPU» — ровно та ловушка, что с нулями сенсоров
    /// в п.38. Культура инвариантная: локаль клиента печатает запятую.</summary>
    private static double? Num(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim().Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Сводка по ряду наблюдений.</summary>
    public static SensorSummary Summarize(IReadOnlyList<SensorSample> samples,
        double loadThreshold = LoadThreshold)
    {
        if (samples.Count == 0)
            return new SensorSummary(0, 0, 0, null, null, null, null, null, null, 0, 0);

        var ordered = samples.OrderBy(s => s.Time).ToList();
        var underLoad = ordered.Where(s => s.CpuPercent >= loadThreshold).ToList();

        // Время под нагрузкой считаем по интервалам между соседними замерами, а не по числу
        // строк: наблюдатель под 100 % CPU замедляется, и «строки × период» врёт.
        double loadedSeconds = 0;
        double maxGap = 0;
        for (var i = 1; i < ordered.Count; i++)
        {
            var delta = (ordered[i].Time - ordered[i - 1].Time).TotalSeconds;
            if (delta > maxGap) maxGap = delta;
            if (ordered[i - 1].CpuPercent >= loadThreshold && ordered[i].CpuPercent >= loadThreshold)
                loadedSeconds += delta;
        }

        return new SensorSummary(
            Samples: ordered.Count,
            LoadedMinutes: Math.Round(loadedSeconds / 60, 1),
            SpanMinutes: Math.Round((ordered[^1].Time - ordered[0].Time).TotalMinutes, 1),
            FirstSample: ordered[0].Time,
            LastSample: ordered[^1].Time,
            MaxCpu: ordered.Max(s => s.CpuPercent),
            AvgCpuUnderLoad: underLoad.Count > 0 ? Math.Round(underLoad.Average(s => s.CpuPercent ?? 0), 1) : null,
            MaxTempC: ordered.Max(s => s.CpuTempC),
            MaxRamPercent: ordered.Max(s => s.RamUsedPercent),
            MaxStressProcesses: ordered.Max(s => s.StressProcesses),
            GapSeconds: Math.Round(maxGap, 1));
    }

    /// <summary>Текстовый отчёт — то, что идёт в вывод CLI и в заметку по СЗ.</summary>
    public static string Format(SensorSummary s)
    {
        if (s.Samples == 0) return "Наблюдений нет: CSV пуст — прогон не подтверждён ничем.";

        var lines = new List<string>
        {
            $"Наблюдений: {s.Samples}, период {s.FirstSample:HH:mm:ss}–{s.LastSample:HH:mm:ss} ({s.SpanMinutes:N1} мин)",
            $"Под нагрузкой (CPU ≥ {LoadThreshold:N0}%): {s.LoadedMinutes:N1} мин — {s.LoadedShare * 100:N0}% времени",
            $"CPU max {s.MaxCpu:N0}%, средний под нагрузкой {s.AvgCpuUnderLoad:N0}%; процессов теста максимум {s.MaxStressProcesses}",
        };
        if (s.MaxTempC is not null) lines.Add($"Температура max {s.MaxTempC:N1} °C");
        if (s.MaxRamPercent is not null) lines.Add($"Память max {s.MaxRamPercent:N0}%");
        if (s.GapSeconds > 30)
            lines.Add($"⚠ Самый длинный разрыв в ряду: {s.GapSeconds:N0} с — наблюдатель тормозил (или машина стояла)");

        // Главные выводы отдельными строками: их читают в первую очередь.
        if (s.MaxStressProcesses == 0)
            lines.Add("⚠ Процессов стресс-тула не видели НИ РАЗУ — прогон, скорее всего, не стартовал.");
        else if (s.LoadedShare < 0.5)
            lines.Add($"⚠ Нагрузка шла лишь {s.LoadedShare * 100:N0}% времени — «машина выстояла N минут» тут неприменимо " +
                      "(так на 160306 40 минут оказались 4.2 минутами нагрузки).");
        else
            lines.Add("Нагрузка подтверждена приборно.");

        return string.Join("\n", lines);
    }
}
