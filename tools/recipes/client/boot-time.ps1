$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Boot-time машины глазами самого клиента — сверка с тем, что показывает hub.
# Нашёл баг: в WinPE таймзона своя (-08:00), boot-time уезжает на 11 часов вперёд,
# uptime в `szcli list` схлопывается в «0сек», а вырубоны по bootTime считаются неверно
# (бэклог п.90). Если hub и этот скрипт расходятся — смотреть в первую очередь на смещение.
#   szcli exec <СЗ> -f tools\recipes\client\boot-time.ps1

$os = Get-CimInstance Win32_OperatingSystem
$boot = $os.LastBootUpTime
$now = Get-Date
"ОС           : $($os.Caption)"
"Boot (local) : $($boot.ToString('o'))"
"Сейчас       : $($now.ToString('o'))"
"Таймзона     : $((Get-TimeZone).Id) (UTC$([TimeZoneInfo]::Local.BaseUtcOffset.ToString('hh\:mm')))"
"Uptime       : {0:N1} мин" -f ($now - $boot).TotalMinutes
# Устойчивый к кривой TZ вариант — так и должен считать агент (бэклог п.90)
"Boot (UTC, от разности локальных времён): {0:o}" -f ([DateTimeOffset]::UtcNow - ($now - $boot))
