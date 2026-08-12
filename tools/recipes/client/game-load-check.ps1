$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Приёмка игрового прогона по ряду lhmmon: идёт ли нагрузка и что было в последние секунды.
# Печатает хвост ряда посекундно + сводку мин/сред/макс по ключевым датчикам.
#
# Грабли (СЗ 160705, 12.08.2026):
#  1. Имена колонок в CSV lhmmon несут суффикс с единицами ('...|Load|GPU Core /%'), поэтому
#     обращение по точному имени возвращает пустоту — колонки ищем регуляркой.
#  2. «Игра запущена» ≠ «нагрузка идёт»: в главном меню CS2 даёт ровно 30 % / 42 Вт, в бою —
#     76 % / 134 Вт. Без этой проверки прогон в меню засчитывается как прогон под нагрузкой.
#  3. Хвост ряда — это и есть картина отказа: на 160705 в последнюю секунду перед hard-off
#     стояли hot spot 84 °C (предел 110) и ровные +12 В, то есть ни перегрева, ни просадки.
#     Смотреть надо именно посекундный хвост, а не средние.
#   szcli exec <СЗ> -f tools\recipes\client\game-load-check.ps1
$Tail = 20     # сколько последних замеров печатать построчно
$Stat = 300    # по скольким последним замерам считать сводку
$Csv  = 'C:\OCCT\sensors.csv'

$g = Get-Process cs2,Cyberpunk2077,OCCT -ErrorAction SilentlyContinue
('процессы нагрузки: ' + $(if ($g) { ($g | ForEach-Object { $_.Name + ' pid ' + $_.Id }) -join ', ' } else { 'НЕТ' }))
('lhmmon: ' + $(if (Get-Process lhmmon -ErrorAction SilentlyContinue) { 'жив' } else { 'НЕ РАБОТАЕТ — ряд не пишется' }))
if (-not (Test-Path $Csv)) { 'ряда нет: ' + $Csv; return }

$head = Get-Content $Csv -TotalCount 1
$rows = (@($head) + (Get-Content $Csv -Tail $Stat)) | ConvertFrom-Csv
$cols = $rows[0].PSObject.Properties.Name
function Col($p) { ($cols | Where-Object { $_ -match $p } | Select-Object -First 1) }

$cT  = $cols[0]
$map = [ordered]@{
    'GPU load, %'      = Col 'Load\|GPU Core'
    'GPU power, Вт'    = Col 'Power\|GPU Package'
    'GPU core, °C'     = Col 'Temperature\|GPU Core'
    'GPU hot spot, °C' = Col 'Temperature\|GPU Hot Spot'
    'VRAM, °C'         = Col 'Temperature\|GPU Memory'
    'GPU clock, МГц'   = Col 'Clock\|GPU Core'
    'GPU fan, %'       = Col 'Control\|GPU Fan'
    'GPU fan, rpm'     = Col 'Fan\|GPU Fan'
    'CPU Tctl, °C'     = Col 'Tctl'
    'CPU power, Вт'    = Col 'Power\|Package'
    '+12V, В'          = Col 'Voltage\|\+12V'
    'Vcore, В'         = Col 'Voltage\|Vcore'
}

('ряд: {0} замеров, окно {1} -> {2}, файл {3:N0} КБ' -f $rows.Count, $rows[0].$cT, $rows[-1].$cT, ((Get-Item $Csv).Length/1KB))

'--- последние замеры ---'
foreach ($r in ($rows | Select-Object -Last $Tail)) {
    ('{0}  GPU {1,4}% {2,5}Вт {3,4}°C hot {4,4}°C {5,5}МГц fan {6,3}% {7,5}rpm | CPU {8,5}°C | +12V {9}' -f `
        ($r.$cT -split ' ')[1], $r.$($map['GPU load, %']), $r.$($map['GPU power, Вт']),
        $r.$($map['GPU core, °C']), $r.$($map['GPU hot spot, °C']), $r.$($map['GPU clock, МГц']),
        $r.$($map['GPU fan, %']), $r.$($map['GPU fan, rpm']), $r.$($map['CPU Tctl, °C']), $r.$($map['+12V, В']))
}

'--- сводка ---'
'{0,-18} {1,8} {2,8} {3,8}' -f 'Показатель','мин','сред','макс'
foreach ($k in $map.Keys) {
    $c = $map[$k]
    if (-not $c) { continue }
    $v = @()
    foreach ($r in $rows) { $x = 0.0; if ([double]::TryParse(($r.$c -replace ',', '.'), [ref]$x)) { $v += $x } }
    if ($v.Count) {
        $m = $v | Measure-Object -Minimum -Maximum -Average
        '{0,-18} {1,8:N1} {2,8:N1} {3,8:N1}' -f $k, $m.Minimum, $m.Average, $m.Maximum
    }
}
$cl = $map['GPU load, %']
if ($cl) {
    $busy = 0
    foreach ($r in $rows) { $x = 0.0; if ([double]::TryParse(($r.$cl -replace ',', '.'), [ref]$x) -and $x -ge 60) { $busy++ } }
    ('GPU >= 60 %: {0} из {1} замеров ({2:N0} %)' -f $busy, $rows.Count, (100.0 * $busy / $rows.Count))
    if ($busy -lt ($rows.Count * 0.3)) { '⚠️ нагрузки почти нет — проверь, не стоит ли игра в меню (см. start-game-cs2.ps1)' }
}
