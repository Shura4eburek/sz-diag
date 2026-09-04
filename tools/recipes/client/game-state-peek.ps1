$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Быстрый взгляд: что с игрой прямо сейчас — стадия запуска и загрузка (СЗ 123456).
#
# Зачем: замер генерации кадров имеет смысл только в устоявшемся геймплее. На компиляции шейдеров
# и на загрузке кадры идут рвано, GPU занят не рендером сцены — вердикт по такой выборке будет
# мусорным. Смотрим время старта, RAM (по ней видно, дошла ли игра до локации) и загрузку GPU.
#
#   szcli exec <СЗ> -f tools\recipes\client\game-state-peek.ps1

$ErrorActionPreference = 'SilentlyContinue'

$p = Get-Process Stalker2-Win64-Shipping
if (-not $p) { '   игра не запущена'; return }
'   pid {0}   старт {1:HH:mm:ss}   в работе {2:N1} мин   RAM {3:N0} МБ' -f `
    $p.Id, $p.StartTime, ((Get-Date) - $p.StartTime).TotalMinutes, ($p.WorkingSet64/1MB)

'=== загрузка GPU по движкам ==='
(Get-Counter '\GPU Engine(*)\Utilization Percentage').CounterSamples |
    Where-Object { $_.InstanceName -match "pid_$($p.Id)_" -and $_.CookedValue -gt 1 } |
    Sort-Object CookedValue -Descending | Select-Object -First 5 |
    ForEach-Object {
        $eng = if ($_.InstanceName -match 'engtype_(\w+)') { $matches[1] } else { '?' }
        '   {0,-12} {1,6:N1}%' -f $eng, $_.CookedValue
    }

'=== загрузка CPU процессом (компиляция шейдеров грузит все ядра) ==='
$c1 = $p.TotalProcessorTime; Start-Sleep -Seconds 3; $p.Refresh()
$busy = ($p.TotalProcessorTime - $c1).TotalSeconds / 3 / [Environment]::ProcessorCount * 100
'   CPU процесса: {0:N1}% от всех ядер ({1} логических)' -f $busy, [Environment]::ProcessorCount

'=== настройки FG в конфиге ==='
Get-Content 'C:\Users\nekit\AppData\Local\Stalker2\Saved\Config\Windows\GSCReXFrameGeneration.ini' |
    Where-Object { $_ -match 'Method|Mode' } | ForEach-Object { '   ' + $_.Trim() }
