$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Кто залогинен + состояние питания/сна + что грузит машину.
# Грабля (161538): GPU-подтесты OCCT не стартуют под SYSTEM в session 0 — прежде чем
# планировать прогон, надо знать, есть ли живая интерактивная сессия и под кем.
# Заодно снимает схему питания и Fast Startup: hard-off в «простое» иногда оказывается
# уходом в сон/гибернацию, а не дефектом.

Write-Output '===== Сессии ====='
(query user 2>&1 | Out-String).Trim()

Write-Output ''
Write-Output '===== Активная схема питания ====='
(powercfg /getactivescheme 2>&1 | Out-String).Trim()
Write-Output '--- таймауты сна/дисплея (AC) ---'
$sub = '238c9fa8-0aad-41ed-83f4-97be242c8f20'   # SUB_SLEEP
$std = '29f6c1db-86da-48c5-9fdb-f2b67b1f44da'   # STANDBYIDLE
$hib = '9d7815a6-7ee4-497e-8888-515a05f02364'   # HIBERNATEIDLE
(powercfg /query SCHEME_CURRENT $sub $std 2>&1 | Select-String 'Current AC' | Out-String).Trim()
(powercfg /query SCHEME_CURRENT $sub $hib 2>&1 | Select-String 'Current AC' | Out-String).Trim()

Write-Output ''
Write-Output '===== Fast Startup / гибернация ====='
$hb = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -ErrorAction SilentlyContinue)
"HiberbootEnabled = $($hb.HiberbootEnabled)  (1 = Fast Startup включён, аптайм врёт)"
"HibernateEnabled = $($hb.HibernateEnabled)"

Write-Output ''
Write-Output '===== Аптайм / последняя загрузка ====='
$os = Get-CimInstance Win32_OperatingSystem
"LastBoot : $($os.LastBootUpTime)"
"Uptime   : $((Get-Date) - $os.LastBootUpTime)"

Write-Output ''
Write-Output '===== ТОП процессов по CPU ====='
(Get-Process | Sort-Object CPU -Descending | Select-Object -First 12 Name, Id, @{n='CPU_s';e={[int]$_.CPU}}, @{n='RAM_MB';e={[int]($_.WorkingSet64/1MB)}} |
    Format-Table -AutoSize | Out-String).Trim()
