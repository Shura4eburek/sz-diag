$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Обороты вентиляторов и помпы СВО в простое: узкий CSV `szcli sensors` их не содержит,
# а жалоба «шумит водянка» без RPM не разбирается вообще.
# Грабля (162003): sensors start даёт 4 колонки (cpu/ram/gpu) — оборотов нет, нужен
# полный lhmmon задачей под SYSTEM с широким CSV.
#   szcli exec <СЗ> -f tools\recipes\client\fan-rpm-snapshot.ps1
$Sz = '162003'

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$lhm  = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\lhmmon\lhmmon.exe'
if (-not (Test-Path $lhm)) { "lhmmon не найден: $lhm"; return }
if (-not (Test-Path 'C:\OCCT')) { New-Item -ItemType Directory -Path 'C:\OCCT' | Out-Null }

$task = "szdiag-lhm-$Sz"
$running = Get-Process lhmmon -ErrorAction SilentlyContinue
if (-not $running) {
    schtasks /delete /tn $task /f 2>$null | Out-Null
    schtasks /create /tn $task /tr "`"$lhm`"" /sc once /st 00:00 /ru SYSTEM /rl highest /f | Out-Null
    schtasks /run /tn $task | Out-Null
    Start-Sleep -Seconds 25
}
$p = Get-Process lhmmon -ErrorAction SilentlyContinue
'lhmmon: ' + $(if ($p) { "жив pid=$($p.Id)" } else { 'НЕ ЗАПУСТИЛСЯ' })

$csv = 'C:\OCCT\sensors.csv'
if (-not (Test-Path $csv)) { 'CSV не создан'; return }
$rows = (@(Get-Content $csv -TotalCount 1) + @(Get-Content $csv -Tail 5)) | ConvertFrom-Csv
$last = $rows[-1]
$cols = $last.PSObject.Properties.Name
"CSV: {0:N1} КБ, колонок {1}" -f ((Get-Item $csv).Length / 1KB), $cols.Count

'== обороты (RPM) и управление (PWM %)'
foreach ($c in $cols) {
    if ($c -match 'Fan|RPM|Pump|Control') {
        $v = $last.$c
        if ($v -ne '' -and $v -ne $null) { "   {0,-52} {1}" -f ($c -replace '\|/[^|]*$',''), $v }
    }
}
'== температуры'
foreach ($c in $cols) {
    if ($c -match 'Temperature' -and $c -notmatch 'Distance|Hot Spot|Memory Junction') {
        $v = $last.$c
        if ($v) { "   {0,-52} {1}" -f ($c -replace '\|/[^|]*$',''), $v }
    }
}
'== нагрузка в момент среза'
foreach ($c in $cols) {
    if ($c -match 'CPU Total|Package\|Power|GPU Core\|Load') { "   {0,-52} {1}" -f ($c -replace '\|/[^|]*$',''), $last.$c }
}
