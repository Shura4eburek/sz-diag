$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Наблюдение за GPU под нагрузкой с одновременным счётчиком TDR (СЗ 161190).
#
# Грабля: 17.08 один TDR положил FurMark посреди прогона, и это заметили только постфактум по
# журналу. Считать события ПОСЛЕ теста мало — важно, в какую минуту оно случилось и был ли жив
# рендер, иначе «тест прошёл 20 минут» и «тест умер на 8-й» выглядят одинаково.
#
# Печатает раз в минуту: жив ли furmark, показания карты и сколько LiveKernelEvent набралось
# за сегодня (0x117 — TDR видеодвижка, 0x1cc — ResourceTimeout).
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-load-tdr-watch.ps1 --timeout 1500
$Minutes = 22
$Proc    = 'furmark'   # ← процесс нагрузки (furmark | cs2 | occt …)
# 168175: счётчик событий включает ретрансляции WER старых дампов (бэклог п.280) —
# поэтому рядом печатаем число НОВЫХ .dmp, созданных после старта наблюдения: это и есть сбой.
$t0 = Get-Date
function NewDumps { @(Get-ChildItem "$env:SystemRoot\LiveKernelReports", "$env:SystemRoot\Minidump" -Recurse -Filter *.dmp -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $t0 }).Count }

$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
function Gpu { (& $smi --query-gpu=pstate,utilization.gpu,clocks.current.graphics,temperature.gpu,fan.speed,power.draw --format=csv,noheader,nounits) -join '' }
function TdrCount {
    $ev = Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1001; StartTime=(Get-Date).Date} -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'LiveKernelEvent' }
    if (-not $ev) { return '0' }
    # код лежит в тексте отчёта как P1 (Properties[2] отдаёт саму строку 'LiveKernelEvent')
    ($ev | ForEach-Object {
        $m = [regex]::Match($_.Message, '(?m)^\s*P1:\s*(\S+)')
        if ($m.Success) { $m.Groups[1].Value } else { '?' }
    } | Group-Object | ForEach-Object { ('0x{0}x{1}' -f $_.Name, $_.Count) }) -join ' '
}

'== старт наблюдения: ' + (Get-Date -Format 'HH:mm:ss') + ', события за сегодня до теста: ' + (TdrCount)
for ($i = 0; $i -lt $Minutes; $i++) {
    $fm = Get-Process $Proc -ErrorAction SilentlyContinue
    $alive = if ($fm) { 'жив' } else { 'НЕТ' }
    '   {0}  {5}: {1,-4} {2}   новых dmp: {4}   события: {3}' -f (Get-Date -Format 'HH:mm:ss'), $alive, (Gpu), (TdrCount), (NewDumps), $Proc
    Start-Sleep -Seconds 60
}
'== конец наблюдения, новых dmp: ' + (NewDumps) + ', события за сегодня: ' + (TdrCount)
