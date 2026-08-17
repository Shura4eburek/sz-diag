$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Кто на машине управляет вентиляторами, кроме BIOS: Afterburner/RTSS/MSI Center/GCC и прочий
# вендорский софт — с временем старта, автозапуском и активными сессиями пользователей.
#
# Грабля (СЗ 161190, «кулери рандомно на максимум»): приборно вентилятор GPU висел на 60-70%
# при 37 °C, и это легко принять за дефект карты. Но на машине оказался Afterburner с
# сохранённым профилем и задачей автозапуска — то есть обороты мог задавать СОФТ КЛИЕНТА,
# а не vBIOS. Пока не ответишь «кто крутит», вердикт по железу выдавать нельзя:
# вопрос решается сравнением оборотов с работающим софтом и без него.
#
#   szcli exec <СЗ> -f tools\recipes\client\fan-control-who.ps1

$names = 'MSIAfterburner|RTSS|RivaTuner|MSI Center|MSI.CentralServer|Dragon|Mystic|GameCenter|' +
         'GCC|Gigabyte|AORUS|EVGA|Precision|SpeedFan|Argus|FanControl|Corsair|iCUE|NZXT|CAM|OpenRGB|SignalRgb'

'== Процессы управления железом сейчас'
$p = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match $names })
if ($p.Count) {
    $p | ForEach-Object {
        $path = try { $_.Path } catch { '' }
        "   {0,-24} pid={1,-6} старт {2:dd.MM HH:mm:ss}  {3}" -f $_.ProcessName, $_.Id, $_.StartTime, $path
    }
}
else { '   ничего из вендорского софта не запущено' }

'== Службы такого софта'
Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $names -or $_.DisplayName -match $names } |
    ForEach-Object { "   {0,-30} {1,-10} {2}" -f $_.Name, $_.Status, $_.StartType }

'== Автозапуск: задачи планировщика'
Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -match $names } | ForEach-Object {
    $i = $_ | Get-ScheduledTaskInfo -ErrorAction SilentlyContinue
    "   {0,-28} {1,-10} last {2}  next {3}" -f $_.TaskName, $_.State, $i.LastRunTime, $i.NextRunTime
}
'== Автозапуск: ключи Run'
foreach ($k in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run') {
    $it = Get-ItemProperty $k -ErrorAction SilentlyContinue
    if ($it) {
        $it.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' } | ForEach-Object {
            "   {0}: {1} = {2}" -f ($k -split '\\')[0], $_.Name, $_.Value
        }
    }
}

'== Кто залогинен (софт вентиляторов живёт только в сессии пользователя)'
(quser 2>&1) | ForEach-Object { "   $_" }

'== Последние логины (4624 type=2/10, 10 шт)'
Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 4624 } -MaxEvents 200 -ErrorAction SilentlyContinue |
    Where-Object { $_.Properties[8].Value -in 2, 10, 11 } | Select-Object -First 10 |
    ForEach-Object { "   {0:dd.MM HH:mm:ss}  user={1} type={2}" -f $_.TimeCreated, $_.Properties[5].Value, $_.Properties[8].Value }
