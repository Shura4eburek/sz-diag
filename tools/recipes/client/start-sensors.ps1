$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Приборный захват lhmmon: посекундный CSV с flush — переживает hard-off.
# Запуск ТОЛЬКО задачей под SYSTEM: под нагрузкой SSH/exec глохнет, а процесс, запущенный
# из сессии агента, умирает вместе с ней. Перед запуском — prep-stress.ps1 (папка + Defender).
#   szcli exec <СЗ> -f tools\recipes\client\start-sensors.ps1
$Sz = '000000'   # ← номер СЗ: попадает в имя задачи, чтобы хвосты было видно в inventory

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$lhm  = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\lhmmon\lhmmon.exe'
$task = "szdiag-lhm-$Sz"

schtasks /delete /tn $task /f 2>$null | Out-Null
"create: " + (schtasks /create /tn $task /tr "`"$lhm`"" /sc once /st 00:00 /ru SYSTEM /rl highest /f 2>&1)
"run:    " + (schtasks /run /tn $task 2>&1)

Start-Sleep -Seconds 25
$p = Get-Process lhmmon -ErrorAction SilentlyContinue
'процесс: ' + $(if ($p) { "жив pid=$($p.Id)" } else { 'НЕ ЗАПУСТИЛСЯ' })

# Приёмка захвата: нули в CPU-колонках = драйвер не поднялся, а не «холодный CPU» (бэклог п.38)
$csv = 'C:\OCCT\sensors.csv'

# Грабля (бэклог п.186): приёмка торопилась — через 25 с после старта задачи файла ещё нет
# (процесс жив, папка на месте, драйвер RUNNING), а «CSV не создан» читалось как ошибка. Ждём
# появления файла с ретраями и различаем три исхода вместо одного поспешного вердикта.
$waited = 0
$RetrySec = 5
$MaxWaitSec = 60
while (-not (Test-Path $csv) -and $waited -lt $MaxWaitSec) {
    Start-Sleep -Seconds $RetrySec
    $waited += $RetrySec
    $p = Get-Process lhmmon -ErrorAction SilentlyContinue
    if (-not $p) { break }   # процесс умер — дальше ждать бессмысленно
}

if (-not (Test-Path $csv)) {
    if (-not $p) {
        "CSV не создан за $waited с, процесс lhmmon МЁРТВ — захват не поднялся, смотри задачу $task"
    } else {
        "CSV ещё не создан за $waited с, процесс жив (pid=$($p.Id)) — захват стартует, дай ему время"
    }
}
if (Test-Path $csv) {
    $rows = (@(Get-Content $csv -TotalCount 1) + @(Get-Content $csv -Tail 3)) | ConvertFrom-Csv
    $cols = $rows[0].PSObject.Properties.Name
    "CSV: {0:N1} КБ, колонок {1}" -f ((Get-Item $csv).Length / 1KB), $cols.Count
    # Единый список для AMD и Intel (бэклог п.160): было заточено под имя AMD-колонки
    # (`Core (Tctl/Tdie)`), на Intel (`CPU Package`, `CPU Core #N`) не срабатывало ни разу —
    # приёмка захвата молчала про температуру, хотя она была в CSV. Список — тот же, что
    # уже проверен в sensors-peek.ps1.
    foreach ($c in $cols) {
        if ($c -match 'Temperature|Tctl|Power|Fan|Clock|Load') {
            "   {0} = {1}" -f ($c -replace '\|/[^|]*$', ''), $rows[-1].$c
        }
    }
}
'драйвер R0lhmmon: ' + ((sc.exe query R0lhmmon 2>&1 | Select-String 'STATE|FAILED' | ForEach-Object { $_.Line.Trim() }) -join ' / ')
