$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Запуск OCCT по готовому расписанию. Две вещи, без которых прогон превращается в часы простоя:
#   1) OCCTCmd при перенаправленном stdout получает EOF и тихо умирает за ~45 с (бэклог п.40)
#      → запуск только «в своём окне» через start /min;
#   2) 3D-тесты рендерят — под SYSTEM в сессии 0 картинки нет, нужен интерактивный токен.
# Поэтому задача клонируется с уже работающей szdiag-occt-<СЗ> (её принципал = залогиненный юзер),
# меняется только файл расписания.
#   szcli exec <СЗ> -f tools\recipes\client\start-occt.ps1
$Sz       = '000000'              # ← номер СЗ
$Schedule = 'schedule-gpu.json'   # ← какое расписание гнать
$Suffix   = 'gpu'                 # ← в имя новой задачи: szdiag-occt<Suffix>-<СЗ>

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$occt = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'
$new  = "szdiag-occt$Suffix-$Sz"

$xml = [xml](schtasks /query /tn "szdiag-occt-$Sz" /xml 2>$null | Out-String)
if (-not $xml.Task) { "задача-донор szdiag-occt-$Sz не найдена — создать её обычным прогоном OCCT"; return }
$xml.Task.Actions.Exec.Arguments =
    '/c start "OCCT ' + $Sz + ' ' + $Suffix + '" /min "' + $occt + '\OCCTCmd.exe" test --schedule="' + $occt + '\' + $Schedule + '" --auto-start'
$tmp = Join-Path $env:TEMP "$new.xml"
$xml.Save($tmp)

schtasks /delete /tn $new /f 2>$null | Out-Null
"create: " + (schtasks /create /tn $new /xml $tmp /f 2>&1)
"run:    " + (schtasks /run /tn $new 2>&1)
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

# 3D-сцена поднимается не мгновенно: на 75-й секунде GPU может ещё стоять на нуле,
# на 4-й минуте нагрузка уже настоящая. Приёмку делать check-load.ps1, не «раз посмотрел».
Start-Sleep -Seconds 75
$p = Get-Process OCCTCmd -ErrorAction SilentlyContinue
'OCCTCmd: ' + $(if ($p) { "жив pid=$($p.Id), CPU-время $([int]$p.CPU) c" } else { 'НЕ ЗАПУСТИЛСЯ' })
if ($p) {
    'дети (здесь и видно, какой тест реально рендерит):'
    Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $p.Id } | ForEach-Object { "   $($_.Name)" }
}
'→ через 3-4 минуты прогнать check-load.ps1: нагрузка засчитывается только по сенсорам'
