# Выжимка из CSV внешнего наблюдателя lhmmon: пики по выбранным сенсорам и покрытие по времени.
# Зачем: lhmmon пишет сотни колонок с именами вида
#   "NVIDIA GeForce RTX 5080|Power|GPU Package|/gpu-nvidia/0/power/0"
# — глазами не читается, а вопрос всегда один: до каких ватт/градусов дошло железо и
# не рвался ли захват (под нагрузкой наблюдатель может быть задавлен — 160697, 10.08:
# 10 точек за 6,2 мин вместо 37, момент вырубона приборно не покрыт).
#
#   .\tools\recipes\host\lhm-csv-summary.ps1 -Path <sensors.csv> [-Match 'RTX 5080|Tctl']
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$Match = 'Power\|GPU Package|Temperature\|GPU Core|Temperature\|Core \(Tctl|Load\|GPU Core|Load\|CPU Total|Load\|Memory'
)

$lines = Get-Content $Path
if ($lines.Count -lt 2) { "в файле нет данных: $Path"; return }
$head = $lines[0] -split ','
$data = @($lines[1..($lines.Count - 1)] | Where-Object { $_ -ne '' } | ForEach-Object { , ($_ -split ',') })

# Покрытие: сколько точек, за какой интервал и был ли разрыв (наблюдатель задавлен нагрузкой).
$times = $data | ForEach-Object { try { [datetime]::Parse($_[0]) } catch { $null } } | Where-Object { $_ }
if ($times) {
    $span = $times[-1] - $times[0]
    'точек: {0}   с {1:HH:mm:ss} по {2:HH:mm:ss} ({3:N1} мин)' -f $data.Count, $times[0], $times[-1], $span.TotalMinutes
    $gaps = for ($i = 1; $i -lt $times.Count; $i++) { ($times[$i] - $times[$i - 1]).TotalSeconds }
    $g = $gaps | Measure-Object -Maximum -Average
    'интервал между точками: средний {0:N1} с, максимальный разрыв {1:N0} с' -f $g.Average, $g.Maximum
}

for ($i = 1; $i -lt $head.Count; $i++) {
    if ($head[$i] -notmatch $Match) { continue }
    $vals = $data | ForEach-Object { $_[$i] } |
        Where-Object { $_ -match '^-?\d' } | ForEach-Object { [double]($_ -replace ',', '.') }
    if (-not $vals) { continue }
    $m = $vals | Measure-Object -Maximum -Average
    $name = ($head[$i] -split '\|')[0..2] -join ' | '
    '{0,-62} max {1,8:N1}   avg {2,8:N1}' -f $name, $m.Maximum, $m.Average
}
