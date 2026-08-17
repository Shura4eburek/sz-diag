$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Обороты вентиляторов и PWM-задания против температур — по CSV lhmmon.
# Грабля (СЗ 161190, жалоба «кулери рандомно виють на максимум»): штатный `szcli sensors`
# пишет только cpu_pct/gpu_temp_c, оборотов в нём НЕТ — на симптом «шумят вентиляторы»
# ответить нечем. lhmmon же тянет Super I/O платы (Nuvoton) и GPU-fan, но 138 колонок
# глазами не читаются, а важен не сам факт «есть RPM», а СВЯЗЬ: ушли обороты вверх при
# росте температуры (штатная кривая) или на холодном железе (дефект датчика/EC/карты).
#
#   szcli exec <СЗ> -f tools\recipes\client\fan-watch.ps1
$Tail = 40   # ← сколько последних замеров печатать построчно

$csv = 'C:\OCCT\sensors.csv'
if (-not (Test-Path $csv)) { 'C:\OCCT\sensors.csv нет — сначала start-sensors.ps1'; return }
$rows = @(Import-Csv $csv)
"CSV: {0:N1} КБ, строк {1}" -f ((Get-Item $csv).Length / 1KB), $rows.Count
if ($rows.Count -lt 2) { 'данных ещё нет'; return }

$tcol = $rows[0].PSObject.Properties.Name | Select-Object -First 1
"окно: {0} → {1}" -f $rows[0].$tcol, $rows[-1].$tcol

# Fan = обороты (RPM), Control = задание в % (PWM). Нужны оба: RPM без PWM не отвечает,
# кто крутит вентилятор — плата по кривой или fail-safe при потере датчика.
$fan = @($rows[0].PSObject.Properties.Name | Where-Object { $_ -match '\|Fan\||\|Control\|' })
$tmp = @($rows[0].PSObject.Properties.Name | Where-Object { $_ -match '\|Temperature\|' -and $_ -notmatch 'Distance to TjMax' })

function Stat($col) {
    $v = foreach ($r in $rows) { $x = $r.$col; if ($x -and $x -ne '' -and $x -ne '[N/A]') { [double]($x -replace ',', '.') } }
    if (-not $v) { return $null }
    $m = $v | Measure-Object -Minimum -Maximum -Average
    [pscustomobject]@{ Col = $col; Now = $v[-1]; Min = $m.Minimum; Max = $m.Maximum; Avg = $m.Average }
}

'--- вентиляторы: сейчас / мин / макс / среднее (мёртвые нули отсеяны) ---'
foreach ($c in $fan) {
    $s = Stat $c
    if (-not $s -or $s.Max -eq 0) { continue }
    "{0,-46} {1,8:N0} {2,8:N0} {3,8:N0} {4,8:N0}" -f ($c -replace '\|/.*$', ''), $s.Now, $s.Min, $s.Max, $s.Avg
}
'--- температуры: сейчас / мин / макс / среднее ---'
foreach ($c in $tmp) {
    $s = Stat $c
    if (-not $s -or $s.Max -eq 0) { continue }
    "{0,-46} {1,8:N1} {2,8:N1} {3,8:N1} {4,8:N1}" -f ($c -replace '\|/.*$', ''), $s.Now, $s.Min, $s.Max, $s.Avg
}

# Построчный хвост: ровно тот вид, по которому видно «обороты скакнули, а температура нет».
$live = @()
foreach ($c in ($fan + $tmp)) { $s = Stat $c; if ($s -and $s.Max -gt 0 -and $s.Max -ne $s.Min) { $live += $c } }
"--- хвост $Tail замеров по колонкам, которые вообще менялись ---"
($live | ForEach-Object { ($_ -split '\|')[-2] + ':' + (($_ -split '\|')[-1] -replace '/.*$', '') }) -join ' | '
$rows | Select-Object -Last $Tail | ForEach-Object {
    $vals = foreach ($c in $live) { '{0,7}' -f $_.$c }
    "{0}  {1}" -f $_.$tcol, ($vals -join '')
}
