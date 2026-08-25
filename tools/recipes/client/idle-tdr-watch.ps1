$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Наблюдение за TDR в ПРОСТОЕ, без касания видеокарты (СЗ 161190).
#
# Грабля: на 161190 все сегодняшние пары 0x117+0x1cc легли ровно на чужие обращения к драйверу —
# загрузка, вход в сессию, открытие панели NVIDIA и даже наш собственный опрос nvidia-smi
# (13:40:25 совпал с замером). Если во время «наблюдения в простое» дёргать nvidia-smi раз в
# минуту, считать будешь свои же события. Поэтому здесь карта не опрашивается вообще: только
# чтение журнала раз в 5 минут, один замер в начале и один в конце.
#
#   szcli exec <СЗ> -f tools\recipes\client\idle-tdr-watch.ps1 --timeout 3000
$Minutes = 45

$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
function Gpu { (& $smi --query-gpu=pstate,utilization.gpu,clocks.current.graphics,temperature.gpu,fan.speed --format=csv,noheader,nounits) -join '' }
function Events {
    $ev = Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1001; StartTime=(Get-Date).Date} -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'LiveKernelEvent' }
    if (-not $ev) { return '0' }
    ($ev | ForEach-Object {
        $m = [regex]::Match($_.Message, '(?m)^\s*P1:\s*(\S+)')
        if ($m.Success) { $m.Groups[1].Value } else { '?' }
    } | Group-Object | ForEach-Object { ('0x{0}x{1}' -f $_.Name, $_.Count) }) -join ' '
}

'== старт простоя {0}: карта {1}' -f (Get-Date -Format 'HH:mm:ss'), (Gpu)
'== события за сегодня на старте: ' + (Events)
'-- дальше карту не трогаем, только журнал раз в 5 минут'
for ($i = 0; $i -lt [int]($Minutes / 5); $i++) {
    Start-Sleep -Seconds 300
    '   {0}  события: {1}' -f (Get-Date -Format 'HH:mm:ss'), (Events)
}
'== конец простоя {0}: карта {1}' -f (Get-Date -Format 'HH:mm:ss'), (Gpu)
