param(
    [Parameter(Mandatory)][string]$Csv,
    [double]$LoadThreshold = 60
)
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Разбор широкого lhmmon-CSV (колонка на датчик) на хосте — после szcli pull.
# Штатный `szcli sensors report` этот формат НЕ понимает и отвечает «CSV пуст — прогон
# не подтверждён ничем», хотя файл валиден (бэклог п.91). Пока не починено — считать здесь.
# Главный вопрос прогона: сколько времени нагрузка РЕАЛЬНО держалась.
#   .\tools\recipes\host\lhm-report.ps1 -Csv <путь к sensors.csv>

$rows = Import-Csv -Path $Csv
if (-not $rows) { 'CSV не разобран'; exit 1 }
$cols = $rows[0].PSObject.Properties.Name
function Pick($pattern) { $cols | Where-Object { $_ -match $pattern } | Select-Object -First 1 }

$sel = [ordered]@{
    'CPU load'  = Pick 'Load\|CPU Total'
    'CPU temp'  = Pick 'Temperature\|Core \(Tctl'
    'CPU power' = Pick 'Power\|Package\|'
    'GPU load'  = Pick 'Load\|GPU Core'
    'GPU temp'  = Pick 'Temperature\|GPU Core'
    'GPU hot'   = Pick 'Temperature\|GPU Hot'
    'GPU power' = Pick 'Power\|GPU Package'
    '+12V'      = Pick 'Voltage\|.*\+12'
    '+5V'       = Pick 'Voltage\|.*\+5'
    '+3.3V'     = Pick 'Voltage\|.*3\.3'
}

$tcol = $cols[0]
$times = foreach ($r in $rows) { try { [datetime]::Parse($r.$tcol, [cultureinfo]::InvariantCulture) } catch {} }
$times = $times | Sort-Object
'Замеров : {0:N0}' -f $rows.Count
'Начало  : {0:dd.MM HH:mm:ss}' -f $times[0]
'Конец   : {0:dd.MM HH:mm:ss}' -f $times[-1]
'Длит.   : {0:N1} мин' -f ($times[-1] - $times[0]).TotalMinutes
$maxGap = 0.0
for ($i = 1; $i -lt $times.Count; $i++) {
    $d = ($times[$i] - $times[$i - 1]).TotalSeconds
    if ($d -gt $maxGap) { $maxGap = $d }
}
# Под 100 % нагрузкой логгер притормаживает — разрыв надо видеть, а не считать «спокойным периодом»
'Макс. разрыв ряда: {0:N1} с' -f $maxGap
''
'{0,-10} {1,10} {2,10} {3,10} {4,8}' -f 'Датчик', 'мин', 'сред', 'макс', 'n'
foreach ($k in $sel.Keys) {
    $c = $sel[$k]
    if (-not $c) { '{0,-10} {1,10}' -f $k, 'нет'; continue }
    $v = foreach ($r in $rows) { if ($r.$c) { [double]::Parse($r.$c, [cultureinfo]::InvariantCulture) } }
    if (-not $v) { '{0,-10} {1,10}' -f $k, 'пусто'; continue }
    $m = $v | Measure-Object -Minimum -Maximum -Average
    '{0,-10} {1,10:N2} {2,10:N2} {3,10:N2} {4,8}' -f $k, $m.Minimum, $m.Average, $m.Maximum, $m.Count
}

foreach ($pair in @(@{ N = 'CPU'; C = $sel['CPU load'] }, @{ N = 'GPU'; C = $sel['GPU load'] })) {
    if (-not $pair.C) { continue }
    $loaded = 0.0
    for ($i = 1; $i -lt $rows.Count; $i++) {
        $a = $rows[$i - 1].($pair.C); $b = $rows[$i].($pair.C)
        if ($a -and $b -and
            [double]::Parse($a, [cultureinfo]::InvariantCulture) -ge $LoadThreshold -and
            [double]::Parse($b, [cultureinfo]::InvariantCulture) -ge $LoadThreshold) {
            $loaded += ([datetime]::Parse($rows[$i].$tcol, [cultureinfo]::InvariantCulture) -
                        [datetime]::Parse($rows[$i - 1].$tcol, [cultureinfo]::InvariantCulture)).TotalSeconds
        }
    }
    ''
    '{0} под нагрузкой (>= {1} %): {2:N1} мин из {3:N1}' -f $pair.N, $LoadThreshold, ($loaded / 60), ($times[-1] - $times[0]).TotalMinutes
}
