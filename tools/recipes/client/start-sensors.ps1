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
if (Test-Path $csv) {
    $rows = (@(Get-Content $csv -TotalCount 1) + @(Get-Content $csv -Tail 3)) | ConvertFrom-Csv
    $cols = $rows[0].PSObject.Properties.Name
    "CSV: {0:N1} КБ, колонок {1}" -f ((Get-Item $csv).Length / 1KB), $cols.Count
    foreach ($c in $cols) {
        if ($c -match 'Power\|Package\||Temperature\|Core \(Tctl|Load\|CPU Total') {
            "   {0} = {1}" -f ($c -replace '\|/[^|]*$', ''), $rows[-1].$c
        }
    }
} else { 'CSV не создан — проверь, что C:\OCCT существует' }
'драйвер R0lhmmon: ' + ((sc.exe query R0lhmmon 2>&1 | Select-String 'STATE|FAILED' | ForEach-Object { $_.Line.Trim() }) -join ' / ')
