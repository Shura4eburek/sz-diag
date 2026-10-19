using System.Globalization;
using System.Text;
using LibreHardwareMonitor.Hardware;

// lhmmon — приборный логгер сенсоров для клиентской машины.
//
// Зачем он есть, хотя у нас уже есть штатный `szcli sensors`: у наблюдателя нет температуры
// CPU там, где нет ACPI-датчика (на 162003 колонка cpu_temp_c пуста на всех замерах — вопрос
// «перегрев или нет» им не закрывается), и нет вольтажей линий питания. LHM читает сенсоры
// через свой kernel-драйвер и отдаёт всё, что видит плата.
//
// Контракт, на который опираются рецепты и `SensorReport.ParseAny` (широкий формат):
//   • имя процесса ровно `lhmmon` — драйвер регистрируется как `R0lhmmon`, его ищут
//     start-sensors.ps1, cleanup-stress.ps1 и `szcli client cleanup`;
//   • CSV по умолчанию `C:\OCCT\sensors.csv`, первая колонка — время;
//   • имена колонок: `<Железо>|<Тип>|<Датчик>|<идентификатор>`, разделитель — ЗАПЯТАЯ
//     (парсер отличает широкий лог от лёгкого по наличию `|` и `,` в шапке);
//   • строка раз в секунду с flush после каждой: лог обязан переживать hard-off, ради
//     которого он и пишется.
//
// Запускать задачей под SYSTEM: драйверу нужны права, а процесс из сессии агента умирает
// вместе с exec'ом (см. start-sensors.ps1).
//
//   lhmmon.exe [путь\к\файлу.csv] [--interval <сек>]

internal static class Program
{
    private static int Main(string[] args)
    {
        var csvPath = @"C:\OCCT\sensors.csv";
        var interval = TimeSpan.FromSeconds(1);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--interval", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
                    interval = TimeSpan.FromSeconds(s);
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                csvPath = args[i];
            }
        }

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = true,
            IsPsuEnabled = true,
        };

        try
        {
            computer.Open();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"не удалось открыть сенсоры (драйвер не поднялся?): {ex.Message}");
            return 2;
        }

        var visitor = new UpdateVisitor();
        computer.Accept(visitor);

        // Список колонок фиксируем ОДИН раз: если часть датчиков появится позже (GPU проснулся),
        // шапка уже написана, и дописывать колонки в существующий CSV нельзя — файл станет
        // нечитаемым для парсера. Такие датчики просто не попадут в этот прогон.
        var sensors = Collect(computer).ToList();
        if (sensors.Count == 0)
        {
            Console.Error.WriteLine("ни одного датчика не найдено — драйвер не загружен или платформа не поддержана");
            computer.Close();
            return 3;
        }

        var dir = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        Console.WriteLine($"lhmmon: датчиков {sensors.Count}, интервал {interval.TotalSeconds:0.#} с, файл {csvPath}");

        using var writer = new StreamWriter(new FileStream(csvPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false));
        writer.WriteLine("Time," + string.Join(",", sensors.Select(s => Escape(Column(s)))));
        writer.Flush();

        var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

        var rows = 0;
        while (!stop.IsSet)
        {
            computer.Accept(visitor);

            var line = new StringBuilder();
            line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            foreach (var s in sensors)
            {
                line.Append(',');
                if (s.Value is { } v) line.Append(v.ToString("0.###", CultureInfo.InvariantCulture));
            }

            writer.WriteLine(line.ToString());
            writer.Flush();   // без этого hard-off съедает последние минуты — ровно то, что ловим
            rows++;

            stop.Wait(interval);
        }

        Console.WriteLine($"lhmmon: остановлен, строк записано {rows}");
        computer.Close();
        return 0;
    }

    /// <summary>Имя колонки в формате широкого лога: `<Железо>|<Тип>|<Датчик>|<идентификатор>`.</summary>
    private static string Column(ISensor s)
        => $"{s.Hardware.Name}|{s.SensorType}|{s.Name}|{s.Identifier}";

    private static string Escape(string value)
        => value.Contains(',') || value.Contains('"')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private static IEnumerable<ISensor> Collect(IComputer computer)
    {
        foreach (var hardware in computer.Hardware)
        {
            foreach (var s in hardware.Sensors) yield return s;
            foreach (var sub in hardware.SubHardware)
                foreach (var s in sub.Sensors) yield return s;
        }
    }

    /// <summary>Обход дерева железа с обновлением значений — так требует LHM.</summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
