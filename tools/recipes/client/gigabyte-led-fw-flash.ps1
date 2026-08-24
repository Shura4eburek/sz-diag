# СЗ 163013: перепрошивка LED-контроллера Gigabyte (IT5711), зависшего в BOOT-режиме
# (048D:57DB = "ITE Upgrade Mode"), из-за чего подсветку не видел НИ ОДИН софт.
#
# Порядок целиком:
#   1. Пакет "GIGABYTE IT5711 RGB lighting controller firmware upgrade package" с сайта платы
#      (download.gigabyte.com/FileList/Utility/mb_utility_ITESHFU_v4.1.36_5711.zip). Сам сайт под
#      Cloudflare и отдаёт 403 и боксу, и клиенту — прямую ссылку добывать через архивный снапшот:
#      curl "https://web.archive.org/web/<ts>id_/<url страницы support>" | grep download.gigabyte.com
#   2. Положить в Hub.ToolsRoot и доставить: szcli push <СЗ> gigabyte-iteshfu
#   3. Этот скрипт — открывает ITESHFU.exe GUI elevated В СЕССИИ ПОЛЬЗОВАТЕЛЯ.
#   4. Кнопку Update жмёт человек у машины. Питание во время заливки не снимать.
#   5. Приёмка: rgb-verify-after-flash.ps1
#
# Почему не автоматом: тихий режим бесполезен — /CHECKDEVICE и /FWVER ВИСНУТ намертво (утилита
# ищет MAIN-устройство 048D:5711, а его нет: контроллер в BOOT), /APPVER отдаёт "AppVer: 0.0.0".
# В пакете 20 разных BIN по 126 КБ (800S_FWID_00/01/02/03/08, WithEC, WithoutEC, TRX50, TACHYON),
# и какой из них подходит плате, утилита показывает ТОЛЬКО в окне. Наугад заливать нельзя — кирпич.
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$dir  = 'C:\Users\User\Desktop\Client-test\tools\gigabyte-iteshfu'
$exe  = Join-Path $dir 'ITESHFU.exe'
$user = (Get-CimInstance Win32_ComputerSystem).UserName
$task = 'szdiag-iteshfu-gui'

Get-Process ITESHFU -ErrorAction SilentlyContinue | Stop-Process -Force
schtasks /delete /tn $task /f 2>$null | Out-Null
schtasks /create /tn $task /tr "`"$exe`"" /sc once /st 23:59 /ru $user /rl highest /it /f | Out-Null
schtasks /run /tn $task | Out-Null

$t = 0
do { Start-Sleep -Seconds 2; $t += 2; $p = Get-Process ITESHFU -ErrorAction SilentlyContinue } while (-not $p -and $t -lt 20)
if ($p) {
  Start-Sleep -Seconds 3
  $p = Get-Process ITESHFU -ErrorAction SilentlyContinue
  Write-Output "ITESHFU запущена: PID $($p.Id), окно: '$($p.MainWindowTitle)', отвечает: $($p.Responding)"
  Write-Output "Окно должно быть на экране машины."
} else {
  Write-Output "процесс не поднялся"
}
schtasks /delete /tn $task /f 2>$null | Out-Null

Write-Output "=== состояние контроллера сейчас ==="
Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'VID_048D' } |
  Select-Object Status,InstanceId | Format-Table -AutoSize | Out-String -Width 120
