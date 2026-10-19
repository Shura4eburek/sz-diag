$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# ОСТАНОВИТЬ ПРОГОН OCCT ПО-ХОРОШЕМУ и посмотреть, сохранился ли отчёт.
#
# Грабля (СЗ 162003, 15.09.2026): `--auto-save-report=true` пишет отчёт ТОЛЬКО при
# штатном выходе. Под SYSTEM (сессия 0) прогон по расписанию не заканчивается — на
# 162003 Memtest молотил 3 ч 40 мин при `Duration=03:00:00`, на 161346 то же было с
# Combined (1 ч 38 мин вместо часа). `Stop-Process -Force` в такой ситуации гарантирует
# потерю результата, поэтому сначала пробуем закрыть мягко (`CloseMainWindow`, затем
# `Stop-ScheduledTask`) и ТОЛЬКО потом, если файла так и нет, снимаем принудительно.
#
#   szcli exec <СЗ> -f tools\recipes\client\occt-stop-graceful.ps1 --param Sz=162003

$Sz     = '162003'   # ← номер СЗ
$Suffix = 'mem'      # ← szdiag-occt<Suffix>-<СЗ>
$Force  = $false     # ← $true: снять принудительно, если мягкий путь не сработал

$task = "szdiag-occt$Suffix-$Sz"
$before = @(Get-ChildItem 'C:\OCCT' -File -ErrorAction SilentlyContinue).Count
"файлов в C:\OCCT до остановки: $before"

$p = Get-Process OCCTCmd, OCCTEnterprise -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { 'процесса OCCT нет — останавливать нечего' }
else {
    'процесс {0} pid {1}, в работе {2:hh\:mm}' -f $p.ProcessName, $p.Id, ((Get-Date) - $p.StartTime)
    if ($p.CloseMainWindow()) { 'послан CloseMainWindow' } else { 'CloseMainWindow не принят (нет окна — процесс в сессии 0)' }
    Start-Sleep -Seconds 20
}

Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
'Stop-ScheduledTask отправлен'
Start-Sleep -Seconds 40

$p = Get-Process OCCTCmd, OCCTEnterprise -ErrorAction SilentlyContinue | Select-Object -First 1
if ($p) {
    'процесс ЖИВ после мягкой остановки: pid {0}' -f $p.Id
    if ($Force) { $p | Stop-Process -Force; 'снят принудительно' }
} else { 'процесс завершился' }

Start-Sleep -Seconds 15
$files = @(Get-ChildItem 'C:\OCCT' -File -ErrorAction SilentlyContinue)
"файлов в C:\OCCT после: $($files.Count)"
$files | ForEach-Object { '   {0:HH:mm:ss} {1,10:N0} б  {2}' -f $_.LastWriteTime, $_.Length, $_.Name }
if (-not $files) { 'ОТЧЁТА НЕТ — этот бинарь в сессии 0 результат не сохраняет, гнать надо интерактивной задачей' }
