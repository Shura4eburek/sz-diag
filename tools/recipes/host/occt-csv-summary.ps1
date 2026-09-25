# Выжимка из CSV-отчёта OCCT: пики температур/мощности и длительность прогона.
# Зачем: HTML-отчёт OCCT на 3 МБ читать глазами нечем, а вопрос всегда один — было ли
# железо на пределе (темпы, ватты, тротлинг) и сколько прогон реально шёл.
#
# Две грабли этого файла (160697, 17.09.2026), из-за которых разбор трижды дал пустые колонки:
#   1. У CSV ДВА заголовка: строка 1 — устройства ("AMD Ryzen 7 7800X3D,..."), строка 2 —
#      имена сенсоров ("Time;,CPU (Tctl/Tdie),..."). Import-Csv берёт первую и теряет всё.
#   2. `$lines | ForEach-Object { $_ -split ',' }` разворачивает результат в ПЛОСКИЙ массив
#      полей: 1400 строк превращаются в 379400 «строк». Нужна запятая-оператор: `,($_ -split ',')`.
#
#   .\tools\recipes\host\occt-csv-summary.ps1 -Path <occt-report.csv>
param([Parameter(Mandatory)][string]$Path)

$lines = Get-Content $Path
if ($lines.Count -lt 3) { "в файле нет данных: $Path"; return }
$head = $lines[1] -split ','
$data = @($lines[2..($lines.Count - 1)] | Where-Object { $_ -ne '' } | ForEach-Object { , ($_ -split ',') })

function Idx([string]$pattern) {
    for ($i = 0; $i -lt $head.Count; $i++) { if ($head[$i].Trim() -match $pattern) { return $i } }
    return -1
}

$want = [ordered]@{
    'CPU (Tctl/Tdie)'       = '^CPU \(Tctl/Tdie\)$'
    'CPU Package Power'     = '^CPU Package Power$'
    'CPU PPT'               = '^CPU PPT$'
    'GPU Temperature'       = '^GPU Temperature$'
    'GPU ASIC Power'        = '^GPU ASIC Power$'
    'Total CPU Usage'       = '^Total CPU Usage$'
    'Physical Memory Load'  = '^Physical Memory Load$'
    'Memory Clock'          = '^Memory Clock$'
    'Thermal Throttl (HTC)' = '^Thermal Throttling \(HTC\)$'
    'PROCHOT CPU'           = '^Thermal Throttling \(PROCHOT CPU\)$'
    'Total Errors'          = '^Total Errors$'
}

'файл: {0}' -f (Split-Path $Path -Leaf)
'точек: {0}   счётчик времени: {1} → {2} с ({3:N0} мин)' -f `
    $data.Count, $data[0][0], $data[-1][0], ([double]$data[-1][0] / 60)

foreach ($k in $want.Keys) {
    $i = Idx $want[$k]
    if ($i -lt 0) { '{0,-22} : колонки нет' -f $k; continue }
    $vals = $data | ForEach-Object { $_[$i] } |
        Where-Object { $_ -match '^-?\d' } | ForEach-Object { [double]($_ -replace ',', '.') }
    if (-not $vals) { '{0,-22} : пусто' -f $k; continue }
    $m = $vals | Measure-Object -Maximum -Minimum -Average
    '{0,-22} : max {1,8:N1}   avg {2,8:N1}   min {3,8:N1}' -f $k, $m.Maximum, $m.Average, $m.Minimum
}
