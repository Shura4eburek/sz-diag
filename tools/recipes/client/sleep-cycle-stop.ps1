$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Остановить цикл сна (парный откат к sleep-cycle-test.ps1, СЗ 161498). Ставит стоп-файл,
# снимает задачу пробуждения и печатает лог теста целиком.
#   szcli exec <СЗ> -f tools\recipes\client\sleep-cycle-stop.ps1
New-Item -ItemType File 'C:\OCCT\stop-sleep-test' -Force | Out-Null
Get-ChildItem 'C:\Windows\System32\Tasks' -Filter 'szdiag-sleepcycle-*' -ErrorAction SilentlyContinue |
    ForEach-Object { schtasks /delete /tn $_.Name /f 2>&1 | Out-Null; "задача снята: $($_.Name)" }
$log = 'C:\OCCT\sleep-test.log'
if (Test-Path $log) { '--- лог теста ---'; Get-Content $log -Encoding UTF8 } else { 'лога нет' }
