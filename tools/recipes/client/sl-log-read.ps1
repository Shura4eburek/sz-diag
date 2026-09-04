$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Что Streamline рассказал о DLSS-G после перезапуска игры (СЗ 123456).
# Читается после stalker2-restart-with-sl-log.ps1.
#
#   szcli exec <СЗ> -f tools\recipes\client\sl-log-read.ps1

$ErrorActionPreference = 'SilentlyContinue'
$dir = 'C:\Users\nekit\AppData\Local\Temp\sl-logs'

'=== процессы игры ==='
Get-Process Stalker2, Stalker2-Win64-Shipping |
    ForEach-Object { '   {0,-26} pid {1,-7} старт {2:HH:mm:ss}  RAM {3:N0} МБ' -f $_.ProcessName, $_.Id, $_.StartTime, ($_.WorkingSet64/1MB) }

'=== файлы логов Streamline ==='
$logs = Get-ChildItem $dir -File | Sort-Object LastWriteTime -Descending
if (-not $logs) { '   логов нет — sl.interposer.json не подхватился или игра ещё не дошла до рендера' }
$logs | ForEach-Object { '   {0,-28} {1,10:N0} б  {2:HH:mm:ss}' -f $_.Name, $_.Length, $_.LastWriteTime }

foreach ($l in ($logs | Select-Object -First 2)) {
    "=== $($l.Name): всё про DLSS-G / отказы ==="
    Get-Content $l.FullName |
        Select-String -Pattern 'dlss_g|dlssg|DLSS-G|frame ?gen|not supported|unsupported|cannot|fail|error|warn|denied|disabl|require|VRAM|reflex|swapchain|HWS|scheduling' |
        Select-Object -First 80 | ForEach-Object { '   ' + $_.Line.Trim() }
}
