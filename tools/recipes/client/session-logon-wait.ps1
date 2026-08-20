$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Есть ли РЕАЛЬНЫЙ вход в сессию (а не экран входа).
#
# Грабля (СЗ 161190, бэклог п.170): проверять лечение на экране входа бессмысленно — виновник
# (LEDKeeper2) стартует только при логине пользователя, и до входа карта честно лежит в P8.
# Ждём explorer.exe, и только потом мерим pstate.
#
#   szcli exec <СЗ> -f tools\recipes\client\session-logon-wait.ps1

$e = @(Get-Process explorer -ErrorAction SilentlyContinue)
if ($e.Count -gt 0) {
    "LOGON: да, explorer {0} шт, вход {1:HH:mm:ss}" -f $e.Count, ($e | Sort-Object StartTime | Select-Object -First 1).StartTime
} else {
    'LOGON: нет — машина на экране входа'
}
$led = @(Get-Process LEDKeeper2 -ErrorAction SilentlyContinue)
if ($led.Count -gt 0) { "LEDKeeper2: pid {0}, старт {1:HH:mm:ss}" -f $led[0].Id, $led[0].StartTime } else { 'LEDKeeper2: не запущен' }
