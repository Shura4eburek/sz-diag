namespace SzDiag.Contracts;

/// <summary>Лёгкий наблюдатель нагрузки на клиенте: CSV раз в N секунд, построчной дозаписью.
///
/// Пять заявок подряд во время прогона наблюдать было нечем, и дважды это едва не привело к
/// неверному выводу: 10-минутный прогон чуть не записали в «тест не отработал» (логов нет,
/// процесса нет, exec молчит), а на 160306 «40 минут выстояла» на деле означало 4.2 минуты
/// нагрузки и 23 минуты простоя — OCCT отработал конечное расписание и вышел (бэклог п.64/п.7).
///
/// Ключевые решения взяты из рабочего обхода, который сработал вживую:
/// * <c>Add-Content</c> на каждой строке (открыл-записал-закрыл) — данные переживают жёсткий
///   вырубон, в отличие от буферизованного вывода (`nvidia-smi -f` оставлял пустой файл, п.20);
/// * только **дешёвые** счётчики: под 100 % нагрузкой сам наблюдатель тормозит, и за 9 минут
///   вместо 54 строк записалось 11 — виноват был `Get-Counter` по GPU;
/// * никакого ring0: LHM/WinRing0 конфликтует с OCCT за драйвер и пишет нули или виснет
///   (п.19/п.23/п.38). Температуры берём из ACPI, если они есть, и молчим, если нет.</summary>
public static class SensorWatcher
{
    /// <summary>Имя CSV-файла наблюдателя внутри папки задачи.</summary>
    public const string CsvName = "sensors.csv";

    /// <summary>Скрипт наблюдателя. Пишет CSV до истечения срока или до вырубона.</summary>
    /// <param name="csvPath">Куда писать (папка должна существовать).</param>
    /// <param name="intervalSeconds">Период опроса.</param>
    /// <param name="minutes">Сколько минут крутиться (0 — пока не убьют).</param>
    /// <param name="processNames">Процессы стресс-тула, наличие которых важно фиксировать:
    /// «нагрузка шла» подтверждается не намерением, а живым процессом и загрузкой CPU.</param>
    public static string BuildScript(string csvPath, int intervalSeconds, int minutes,
        IReadOnlyList<string> processNames)
    {
        var procs = string.Join(",", processNames.Select(p => $"'{p}'"));
        var csvPathEscaped = csvPath.Replace("'", "''");
        var deadline = minutes > 0
            ? $"$deadline = (Get-Date).AddMinutes({minutes})"
            : "$deadline = (Get-Date).AddYears(1)";

        // $$""" — чтобы фигурные скобки PowerShell оставались собой, а подстановки шли как {{…}}.
        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $csv = '{{csvPathEscaped}}'
            $procNames = @({{procs}})
            {{deadline}}
            if (-not (Test-Path $csv)) {
                'time;cpu_pct;stress_procs;cpu_temp_c;ram_used_pct' | Out-File -FilePath $csv -Encoding utf8
            }
            while ((Get-Date) -lt $deadline) {
                # Win32_Processor.LoadPercentage - дешёвый счётчик; счётчики производительности
                # под 100% нагрузкой сами становятся узким местом и рвут ряд наблюдений.
                $cpu = (Get-CimInstance Win32_Processor).LoadPercentage
                if ($cpu -is [array]) { $cpu = ($cpu | Measure-Object -Average).Average }
                $running = 0
                foreach ($n in $procNames) { $running += @(Get-Process -Name $n -ErrorAction SilentlyContinue).Count }
                $temp = ''
                $tz = Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction SilentlyContinue
                if ($tz) { $temp = [math]::Round((($tz | Measure-Object -Property CurrentTemperature -Maximum).Maximum / 10) - 273.15, 1) }
                $os = Get-CimInstance Win32_OperatingSystem
                $ram = ''
                if ($os -and $os.TotalVisibleMemorySize) {
                    $ram = [math]::Round(100 - ($os.FreePhysicalMemory / $os.TotalVisibleMemorySize * 100))
                }
                # Add-Content открывает и закрывает файл на каждой строке: пережить вырубон
                # важнее, чем сэкономить на вводе-выводе.
                ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ';' + $cpu + ';' + $running + ';' + $temp + ';' + $ram) |
                    Add-Content -Path $csv -Encoding utf8
                Start-Sleep -Seconds {{intervalSeconds}}
            }
            """;
    }
}
