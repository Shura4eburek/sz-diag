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
                'time;cpu_pct;stress_procs;cpu_temp_c;ram_used_pct;gpu_pct;gpu_temp_c;gpu_power_w' | Out-File -FilePath $csv -Encoding utf8
            }
            # Поднимаем приоритет наблюдателя: под 100% нагрузкой общий пул потоков CIM/WMI
            # (Get-CimInstance) сам становится узким местом, и cpu_pct/ram_used_pct уходят
            # пустыми на минуты (бэклог п.206, СЗ 161716). Не решает целиком, но снижает шанс
            # голодания наблюдателя наравне со стресс-тулом.
            try { (Get-Process -Id $PID).PriorityClass = 'AboveNormal' } catch {}
            # nvidia-smi лежит в System32 и работает даже из session 0 (проверено на 161312).
            # Без GPU-колонок 30 минут FurMark выглядели как «нагрузка шла 2% времени» — по
            # одному только CPU (бэклог п.80). Нет nvidia-smi (AMD/Intel) - колонки пустые.
            $smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
            $hasSmi = Test-Path $smi
            # nvidia-smi отдаёт "[N/A]" на картах без телеметрии мощности (RTX 3050, бэклог п.166) —
            # писать это в CSV как значение нельзя, иначе szcli sensors report валится на приведении
            # к double. Нечисловое (в т.ч. голый "-") превращаем в явный маркер n/a — не пустую
            # строку: пропуск должен быть виден глазами в файле, а не только логике парсера
            # (бэклог п.206 п.3 — раньше пустая ячейка молча читалась как «данных нет», что
            # неотличимо от ошибки форматирования).
            function ScrubNum([string]$v) {
                if (-not $v) { return 'n/a' }
                $v = $v.Trim()
                if ($v -eq '' -or $v -eq '-' -or $v -match '(?i)^\[?n/?a\]?$') { 'n/a' } else { $v }
            }
            # Счётчик, доступный ниже него в поток stdout — это же и есть «наблюдатель жив»:
            # ExecJobStatus.LastOutputAt следит за файлом стдаута фоновой задачи тем же
            # механизмом, что уже проверен под нагрузкой (бэклог п.208), поэтому `sensors status`
            # может судить о свежести без отдельного похода за CSV по сети.
            $i = 0
            while ((Get-Date) -lt $deadline) {
                $i++
                # Win32_Processor.LoadPercentage - дешёвый счётчик; счётчики производительности
                # под 100% нагрузкой сами становятся узким местом и рвут ряд наблюдений. Один
                # ретрай с короткой паузой — счётчик под пиковой нагрузкой иногда отвечает
                # со второго раза, а не мёртв насовсем.
                $cpu = $null
                for ($try = 0; $try -lt 2 -and $null -eq $cpu; $try++) {
                    try {
                        $cpu = (Get-CimInstance Win32_Processor -ErrorAction Stop).LoadPercentage
                        if ($cpu -is [array]) { $cpu = ($cpu | Measure-Object -Average).Average }
                    } catch { Start-Sleep -Milliseconds 300 }
                }
                if ($null -eq $cpu) { $cpu = 'n/a' }
                $running = 0
                foreach ($n in $procNames) { $running += @(Get-Process -Name $n -ErrorAction SilentlyContinue).Count }
                $temp = 'n/a'
                $tz = Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction SilentlyContinue
                if ($tz) { $temp = [math]::Round((($tz | Measure-Object -Property CurrentTemperature -Maximum).Maximum / 10) - 273.15, 1) }
                $ram = $null
                for ($try = 0; $try -lt 2 -and $null -eq $ram; $try++) {
                    try {
                        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
                        if ($os -and $os.TotalVisibleMemorySize) {
                            $ram = [math]::Round(100 - ($os.FreePhysicalMemory / $os.TotalVisibleMemorySize * 100))
                        }
                    } catch { Start-Sleep -Milliseconds 300 }
                }
                if ($null -eq $ram) { $ram = 'n/a' }
                $gpu = 'n/a'; $gpuTemp = 'n/a'; $gpuPower = 'n/a'
                if ($hasSmi) {
                    $line = & $smi --query-gpu=utilization.gpu,temperature.gpu,power.draw --format=csv,noheader,nounits 2>$null | Select-Object -First 1
                    if ($line) {
                        $p = $line -split ','
                        $gpu = ScrubNum $p[0]; $gpuTemp = ScrubNum $p[1]; $gpuPower = ScrubNum $p[2]
                    }
                }
                # Add-Content открывает и закрывает файл на каждой строке: пережить вырубон
                # важнее, чем сэкономить на вводе-выводе.
                ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ';' + $cpu + ';' + $running + ';' + $temp + ';' + $ram +
                 ';' + $gpu + ';' + $gpuTemp + ';' + $gpuPower) |
                    Add-Content -Path $csv -Encoding utf8
                # Хартбит в stdout фоновой задачи — единственное, что `sensors status` читает
                # без похода за CSV (см. комментарий выше про LastOutputAt).
                Write-Output ("tick;{0};{1}" -f $i, (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
                Start-Sleep -Seconds {{intervalSeconds}}
            }
            """;
    }
}
