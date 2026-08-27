# Рецепт: мгновенный снимок датчиков без LHM (CPU частота/производительность, GPU-загрузка через perf-счётчики,
# темп CPU через AMD SMU недоступен — берём что есть в WMI). Грабля: lhmmon нет в каталоге push, а «сними датчики»
# нужно прямо сейчас (123123, длинная компиляция шейдеров).
$ErrorActionPreference='SilentlyContinue'
"=== $(Get-Date -Format 'HH:mm:ss') ==="
$p = Get-CimInstance Win32_Processor
"CPU: load=$($p.LoadPercentage)% CurrentClock=$($p.CurrentClockSpeed) MHz (Max=$($p.MaxClockSpeed))"
$c = Get-Counter '\Processor Information(_Total)\% Processor Performance','\Processor Information(_Total)\% Processor Utility','\Memory\Available MBytes' -SampleInterval 2 -MaxSamples 1
$c.CounterSamples | % { "  $($_.Path.Split('\')[-1]) = $([math]::Round($_.CookedValue,1))" }
"GPU engine (top 5 по загрузке):"
(Get-Counter '\GPU Engine(*)\Utilization Percentage').CounterSamples | ? CookedValue -gt 1 | sort CookedValue -Desc | select -First 5 | % { "  $([math]::Round($_.CookedValue,1))%  $($_.InstanceName)" }
"GPU mem:"
(Get-Counter '\GPU Adapter Memory(*)\Dedicated Usage').CounterSamples | ? CookedValue -gt 0 | % { "  $([math]::Round($_.CookedValue/1MB)) MB  $($_.InstanceName)" }
"Термозоны:"
Get-CimInstance -Namespace root/wmi MSAcpi_ThermalZoneTemperature | % { "  $($_.InstanceName): $([math]::Round($_.CurrentTemperature/10-273.15,1)) C" }
"Топ процессов по CPU (5):"
Get-Process | sort CPU -Desc | select -First 5 | % { "  {0,-28} cpu_s={1,7:N0} thr={2,4} ws={3,6:N0}MB" -f $_.ProcessName,$_.CPU,$_.Threads.Count,($_.WorkingSet64/1MB) }
"Диски (очередь/активность):"
(Get-Counter '\PhysicalDisk(*)\% Disk Time','\PhysicalDisk(*)\Current Disk Queue Length').CounterSamples | ? { $_.InstanceName -ne '_total' -and $_.CookedValue -gt 0.5 } | % { "  $($_.InstanceName) $($_.Path.Split('\')[-1]) = $([math]::Round($_.CookedValue,1))" }
