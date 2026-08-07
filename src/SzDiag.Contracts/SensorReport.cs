using System.Globalization;

namespace SzDiag.Contracts;

/// <summary>Одна строка наблюдателя. Поля сверх первых пяти появляются только у широкого
/// CSV от `lhmmon` (колонка на датчик) — у лёгкого наблюдателя их нет и быть не должно.</summary>
public sealed record SensorSample(
    DateTime Time,
    double? CpuPercent,
    int StressProcesses,
    double? CpuTempC,
    double? RamUsedPercent,
    double? GpuPercent = null,
    double? GpuTempC = null,
    double? CpuPowerW = null,
    double? GpuPowerW = null,
    double? Volt12 = null,
    double? Volt5 = null,
    double? Volt33 = null);

/// <summary>Какой CSV нам дали. `Unknown` — это НЕ «прогон не подтверждён»: это «мы не поняли
/// файл», и разница принципиальна (бэклог п.91).</summary>
public enum SensorCsvFormat
{
    Unknown,
    /// <summary>Лёгкий наблюдатель `szcli sensors` (`time;cpu_pct;stress_procs;…`).</summary>
    Watcher,
    /// <summary>Широкий лог `lhmmon` / LibreHardwareMonitor: запятая, колонка на датчик.</summary>
    LibreHardwareMonitor,
}

/// <summary>Итог разбора файла: формат + строки. Пустой список при известном формате означает
/// «данных нет», при `Unknown` — «файл не наш».</summary>
public sealed record SensorParse(SensorCsvFormat Format, IReadOnlyList<SensorSample> Samples);

/// <summary>Статистика по линии питания. Отдельно min/max и перцентили: на 160705 четыре
/// замера из 5659 дали «+12V max 49,14 В» — артефакт чтения чипа NCT6687D, который читается
/// как выброс питания и разворачивает диагноз (бэклог п.98).</summary>
/// <param name="Rejected">Сколько замеров отброшено как физически невозможные. Печатается
/// всегда: молча выкинуть — значит спрятать реальный сбой.</param>
public sealed record RailStats(
    string Name,
    double Min,
    double Max,
    double P1,
    double P99,
    int Samples,
    int Rejected);

/// <summary>Датчик, который за весь ряд не изменился ни разу. На 160306 `cpu_temp_c` равнялась
/// 27.9 и на холостом ходу, и десять минут под 100 % CPU: `MSAcpi_ThermalZoneTemperature` на
/// этой плате отдаёт константу-заглушку. По такому «максимуму» тепловой отказ исключается
/// одним взглядом, хотя измерения не было вовсе (бэклог п.71).</summary>
public sealed record ConstantSensor(string Name, double Value, int Samples);

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
/// <param name="GpuLoadedMinutes">Сколько минут грузился GPU. Без этого FurMark-прогон
/// выглядел как «нагрузка шла 2 % времени» — по CPU-порогу (бэклог п.80).</param>
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
    double GapSeconds,
    double GpuLoadedMinutes = 0,
    double? MaxGpu = null,
    double? AvgGpuUnderLoad = null,
    double? MaxGpuTempC = null,
    double? MaxCpuPowerW = null,
    double? MaxGpuPowerW = null,
    IReadOnlyList<RailStats>? Rails = null,
    IReadOnlyList<ConstantSensor>? ConstantSensors = null,
    SensorCsvFormat Format = SensorCsvFormat.Watcher)
{
    /// <summary>Доля времени под нагрузкой — «тест реально грузил железо» в одну цифру.</summary>
    public double LoadedShare => SpanMinutes > 0 ? LoadedMinutes / SpanMinutes : 0;

    /// <summary>Доля времени под нагрузкой GPU.</summary>
    public double GpuLoadedShare => SpanMinutes > 0 ? GpuLoadedMinutes / SpanMinutes : 0;

    /// <summary>Грузилось хоть что-то: CPU или GPU. Ответ «прогон шёл» не должен зависеть от
    /// того, какой именно тул гоняли.</summary>
    public double AnyLoadedMinutes => Math.Max(LoadedMinutes, GpuLoadedMinutes);

    public double AnyLoadedShare => SpanMinutes > 0 ? AnyLoadedMinutes / SpanMinutes : 0;
}

public static class SensorReport
{
    /// <summary>Порог, с которого считаем, что нагрузка идёт. 60 % — с запасом ниже 90 %,
    /// чтобы не потерять прогоны с частично загруженным CPU (memory-тесты).</summary>
    public const double LoadThreshold = 60;

    /// <summary>Физические границы линий питания. Всё, что вне — артефакт чтения чипа, а не
    /// показание (бэклог п.98).</summary>
    public static IReadOnlyDictionary<string, (double Min, double Max)> RailLimits { get; } =
        new Dictionary<string, (double, double)>
        {
            ["+12V"] = (10.0, 14.0),
            ["+5V"] = (4.0, 6.0),
            ["+3.3V"] = (2.5, 4.0),
        };

    /// <summary>Разбирает CSV наблюдателя (см. <see cref="SensorWatcher"/>).</summary>
    public static IReadOnlyList<SensorSample> Parse(string csv) => ParseAny(csv).Samples;

    /// <summary>Разбор с определением формата: лёгкий наблюдатель или широкий лог `lhmmon`.
    /// Раньше понимался только первый, и валидный пятичасовой лог на 5.7 МБ давал
    /// «CSV пуст — прогон не подтверждён ничем» (бэклог п.91).</summary>
    public static SensorParse ParseAny(string csv)
    {
        var text = csv ?? "";
        var header = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

        if (header.StartsWith("time;", StringComparison.OrdinalIgnoreCase))
            return new SensorParse(SensorCsvFormat.Watcher, ParseWatcher(text));

        if (LooksLikeLhm(header))
            return new SensorParse(SensorCsvFormat.LibreHardwareMonitor, ParseLhm(text, header));

        // Шапки нет вовсе — пробуем как наш формат: старые CSV писались без неё.
        var fallback = ParseWatcher(text);
        return fallback.Count > 0
            ? new SensorParse(SensorCsvFormat.Watcher, fallback)
            : new SensorParse(SensorCsvFormat.Unknown, Array.Empty<SensorSample>());
    }

    /// <summary>Шапка широкого лога: имена датчиков вида
    /// <c>AMD Ryzen 5 5500|Load|CPU Total|/amdcpu/0/load/0</c>, разделитель — запятая.</summary>
    private static bool LooksLikeLhm(string header)
        => header.Contains('|') && header.Contains(',');

    private static IReadOnlyList<SensorSample> ParseWatcher(string csv)
    {
        var samples = new List<SensorSample>();
        foreach (var raw in csv.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("time;", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line.Split(';');
            if (parts.Length < 3) continue;
            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var time)) continue;

            // Колонки 5-7 (GPU) появились позже — старые CSV просто короче (бэклог п.80).
            samples.Add(new SensorSample(
                time,
                Num(parts.ElementAtOrDefault(1)),
                (int)(Num(parts.ElementAtOrDefault(2)) ?? 0),
                Num(parts.ElementAtOrDefault(3)),
                Num(parts.ElementAtOrDefault(4)),
                GpuPercent: Num(parts.ElementAtOrDefault(5)),
                GpuTempC: Num(parts.ElementAtOrDefault(6)),
                GpuPowerW: Num(parts.ElementAtOrDefault(7))));
        }
        return samples;
    }

    private static IReadOnlyList<SensorSample> ParseLhm(string csv, string header)
    {
        var cols = SplitCsv(header);
        int Find(params string[] needles)
        {
            for (var i = 0; i < cols.Count; i++)
            {
                var name = cols[i];
                if (needles.All(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) return i;
            }
            return -1;
        }

        var iCpuLoad = Find("Load|", "CPU Total");
        var iCpuTemp = Find("Temperature|", "Tctl");
        if (iCpuTemp < 0) iCpuTemp = Find("Temperature|", "CPU Package");
        var iCpuPower = Find("Power|", "Package");
        var iGpuLoad = Find("Load|", "GPU Core");
        var iGpuTemp = Find("Temperature|", "GPU Core");
        var iGpuPower = Find("Power|", "GPU Package");
        var iRam = Find("Load|", "Memory");
        var i12 = Find("Voltage|", "+12");
        var i5 = Find("Voltage|", "+5");
        var i33 = Find("Voltage|", "3.3");

        var samples = new List<SensorSample>();
        var first = true;
        foreach (var raw in csv.Split('\n'))
        {
            if (first) { first = false; continue; }   // шапка
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;

            var parts = SplitCsv(line);
            if (parts.Count == 0) continue;
            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var time)) continue;

            double? At(int idx) => idx >= 0 && idx < parts.Count ? Num(parts[idx]) : null;

            samples.Add(new SensorSample(
                time,
                At(iCpuLoad),
                // Широкий лог не знает про процессы стресс-тула — это поле лёгкого наблюдателя.
                0,
                At(iCpuTemp),
                At(iRam),
                At(iGpuLoad),
                At(iGpuTemp),
                At(iCpuPower),
                At(iGpuPower),
                At(i12),
                At(i5),
                At(i33)));
        }
        return samples;
    }

    /// <summary>Разбор строки CSV с учётом кавычек: имена датчиков содержат запятые.</summary>
    private static IReadOnlyList<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ',' && !inQuotes) { result.Add(current.ToString().Trim()); current.Clear(); continue; }
            current.Append(c);
        }
        result.Add(current.ToString().Trim());
        return result;
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
        double loadThreshold = LoadThreshold, SensorCsvFormat format = SensorCsvFormat.Watcher)
    {
        if (samples.Count == 0)
            return new SensorSummary(0, 0, 0, null, null, null, null, null, null, 0, 0, Format: format);

        var ordered = samples.OrderBy(s => s.Time).ToList();
        var underLoad = ordered.Where(s => s.CpuPercent >= loadThreshold).ToList();
        var gpuUnderLoad = ordered.Where(s => s.GpuPercent >= loadThreshold).ToList();

        // Время под нагрузкой считаем по интервалам между соседними замерами, а не по числу
        // строк: наблюдатель под 100 % CPU замедляется, и «строки × период» врёт.
        double loadedSeconds = 0;
        double gpuLoadedSeconds = 0;
        double maxGap = 0;
        for (var i = 1; i < ordered.Count; i++)
        {
            var delta = (ordered[i].Time - ordered[i - 1].Time).TotalSeconds;
            if (delta > maxGap) maxGap = delta;
            if (ordered[i - 1].CpuPercent >= loadThreshold && ordered[i].CpuPercent >= loadThreshold)
                loadedSeconds += delta;
            if (ordered[i - 1].GpuPercent >= loadThreshold && ordered[i].GpuPercent >= loadThreshold)
                gpuLoadedSeconds += delta;
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
            GapSeconds: Math.Round(maxGap, 1),
            GpuLoadedMinutes: Math.Round(gpuLoadedSeconds / 60, 1),
            MaxGpu: ordered.Max(s => s.GpuPercent),
            AvgGpuUnderLoad: gpuUnderLoad.Count > 0 ? Math.Round(gpuUnderLoad.Average(s => s.GpuPercent ?? 0), 1) : null,
            MaxGpuTempC: ordered.Max(s => s.GpuTempC),
            MaxCpuPowerW: ordered.Max(s => s.CpuPowerW),
            MaxGpuPowerW: ordered.Max(s => s.GpuPowerW),
            Rails: BuildRails(ordered),
            ConstantSensors: FindConstants(ordered),
            Format: format);
    }

    /// <summary>Статистика по линиям питания с отсевом физически невозможных значений.</summary>
    private static IReadOnlyList<RailStats> BuildRails(IReadOnlyList<SensorSample> samples)
    {
        var rails = new List<RailStats>();
        Add("+12V", samples.Select(s => s.Volt12));
        Add("+5V", samples.Select(s => s.Volt5));
        Add("+3.3V", samples.Select(s => s.Volt33));
        return rails;

        void Add(string name, IEnumerable<double?> values)
        {
            var (min, max) = RailLimits[name];
            var all = values.Where(v => v is not null).Select(v => v!.Value).ToList();
            if (all.Count == 0) return;

            var good = all.Where(v => v >= min && v <= max).OrderBy(v => v).ToList();
            var rejected = all.Count - good.Count;
            if (good.Count == 0)
            {
                // Все значения вне диапазона — это уже не шум чипа, а либо чужая линия,
                // либо настоящий сбой: молчать нельзя.
                rails.Add(new RailStats(name, all.Min(), all.Max(), all.Min(), all.Max(), all.Count, rejected));
                return;
            }
            rails.Add(new RailStats(name, good[0], good[^1],
                Percentile(good, 0.01), Percentile(good, 0.99), good.Count, rejected));
        }
    }

    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var idx = (int)Math.Round(p * (sorted.Count - 1));
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    /// <summary>Датчики-константы: значение не изменилось ни разу за весь ряд.</summary>
    private static IReadOnlyList<ConstantSensor> FindConstants(IReadOnlyList<SensorSample> samples)
    {
        var found = new List<ConstantSensor>();
        Check("температура CPU", samples.Select(s => s.CpuTempC));
        Check("температура GPU", samples.Select(s => s.GpuTempC));
        return found;

        void Check(string name, IEnumerable<double?> values)
        {
            var all = values.Where(v => v is not null).Select(v => v!.Value).ToList();
            // Меньше десяти замеров — рано делать вывод: ряд может быть просто коротким.
            if (all.Count < 10) return;
            if (all.Distinct().Count() == 1) found.Add(new ConstantSensor(name, all[0], all.Count));
        }
    }

    /// <summary>Текстовый отчёт — то, что идёт в вывод CLI и в заметку по СЗ.</summary>
    public static string Format(SensorSummary s)
    {
        if (s.Format == SensorCsvFormat.Unknown)
            return "Формат CSV не распознан: ожидался лог `szcli sensors` (шапка time;cpu_pct;…) "
                   + "или широкий лог lhmmon (запятая, имена датчиков через |). "
                   + "Утверждать что-либо о прогоне по этому файлу нельзя.";

        if (s.Samples == 0) return "Наблюдений нет: CSV пуст — прогон не подтверждён ничем.";

        var lines = new List<string>
        {
            $"Наблюдений: {s.Samples}, период {s.FirstSample:HH:mm:ss}–{s.LastSample:HH:mm:ss} ({s.SpanMinutes:N1} мин)",
            $"Под нагрузкой (CPU ≥ {LoadThreshold:N0}%): {s.LoadedMinutes:N1} мин — {s.LoadedShare * 100:N0}% времени",
            $"CPU max {s.MaxCpu:N0}%, средний под нагрузкой {s.AvgCpuUnderLoad:N0}%; процессов теста максимум {s.MaxStressProcesses}",
        };

        // GPU-строка обязательна: FurMark-прогон по CPU-порогу выглядит как «нагрузка шла 2 %
        // времени», хотя видеокарта стояла в потолке полчаса (бэклог п.80).
        if (s.MaxGpu is not null)
        {
            lines.Add($"Под нагрузкой (GPU ≥ {LoadThreshold:N0}%): {s.GpuLoadedMinutes:N1} мин — {s.GpuLoadedShare * 100:N0}% времени");
            var gpuTail = s.MaxGpuTempC is not null ? $", температура max {s.MaxGpuTempC:N1} °C" : "";
            var gpuPower = s.MaxGpuPowerW is not null ? $", мощность max {s.MaxGpuPowerW:N0} Вт" : "";
            lines.Add($"GPU max {s.MaxGpu:N0}%{gpuTail}{gpuPower}");
        }

        var constants = s.ConstantSensors ?? Array.Empty<ConstantSensor>();
        var constantTemp = constants.FirstOrDefault(c => c.Name.Contains("температура CPU"));
        if (constantTemp is not null)
            lines.Add($"⚠ Температура CPU: датчик не отвечает (константа {constantTemp.Value:N1} °C на всех "
                      + $"{constantTemp.Samples} замерах) — перегрев по этим данным ни подтвердить, ни исключить нельзя.");
        else if (s.MaxTempC is not null)
            lines.Add($"Температура max {s.MaxTempC:N1} °C");

        if (s.MaxCpuPowerW is not null) lines.Add($"Мощность CPU max {s.MaxCpuPowerW:N0} Вт");
        if (s.MaxRamPercent is not null) lines.Add($"Память max {s.MaxRamPercent:N0}%");

        foreach (var rail in s.Rails ?? Array.Empty<RailStats>())
        {
            var rejected = rail.Rejected > 0
                ? $" ({rail.Rejected} замер(ов) вне физического диапазона отброшено)"
                : "";
            lines.Add($"{rail.Name}: {rail.Min:N3}…{rail.Max:N3} В, 1–99 перцентиль {rail.P1:N3}…{rail.P99:N3} В{rejected}");
        }

        if (s.GapSeconds > 30)
            lines.Add($"⚠ Самый длинный разрыв в ряду: {s.GapSeconds:N0} с — наблюдатель тормозил (или машина стояла)");

        // Главные выводы отдельными строками: их читают в первую очередь.
        if (s.MaxStressProcesses == 0 && s.Format == SensorCsvFormat.Watcher)
            lines.Add("⚠ Процессов стресс-тула не видели НИ РАЗУ — прогон, скорее всего, не стартовал.");
        else if (s.AnyLoadedShare < 0.5)
            lines.Add($"⚠ Нагрузка шла лишь {s.AnyLoadedShare * 100:N0}% времени — «машина выстояла N минут» тут неприменимо " +
                      "(так на 160306 40 минут оказались 4.2 минутами нагрузки).");
        else
            lines.Add("Нагрузка подтверждена приборно.");

        return string.Join("\n", lines);
    }
}
