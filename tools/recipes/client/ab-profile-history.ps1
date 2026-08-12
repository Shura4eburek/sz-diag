$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Кто и когда трогал настройки MSI Afterburner (разгон, лимит мощности, ручной вентилятор).
#
# Зачем (СЗ 160705, 12.08.2026): в претензионной заявке важно, чьи настройки действовали в момент
# отказов — клиента или сервиса. «Кто» в смысле человека Windows не пишет: аудит доступа к файлам
# по умолчанию выключен, событий «пользователь изменил настройку» не существует. Восстанавливается
# косвенно — по времени правок файлов, запускам самого Afterburner и тому, что происходило рядом.
#
# Что уже вскрывал на 160705: правка профиля карты 04.08 16:01 легла ровно на переустановку
# драйвера AMD сервисом (15:55–15:59) — то есть часть «сброса профиля» сделали мы, а не клиент.
#   szcli exec <СЗ> -f tools\recipes\client\ab-profile-history.ps1
$dir = 'C:\Program Files (x86)\MSI Afterburner'

'=== 1. Время правки файлов Afterburner ==='
if (Test-Path $dir) {
    Get-ChildItem $dir -Recurse -Include *.cfg -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 15 |
        ForEach-Object { '{0:dd.MM.yyyy HH:mm:ss}  {1,10:N0} б  {2}' -f $_.LastWriteTime, $_.Length, $_.FullName.Replace($dir,'') }
} else { 'Afterburner не установлен' }

'=== 2. Значения профиля карты (что именно задано) ==='
Get-ChildItem (Join-Path $dir 'Profiles') -Filter 'VEN_*.cfg' -ErrorAction SilentlyContinue | ForEach-Object {
    ('== ' + $_.Name + '  (' + $_.LastWriteTime.ToString('dd.MM.yyyy HH:mm') + ')')
    Get-Content $_.FullName | Where-Object { $_ -match '^(CoreClk|MemClk|PowerLimit|TempLimit|FanMode|FanSpeed)=' } |
        ForEach-Object { '   ' + $_ }
}

'=== 3. Когда Afterburner появился на машине ==='
$first = Get-ChildItem $dir -ErrorAction SilentlyContinue | Sort-Object CreationTime | Select-Object -First 1
if ($first) { ('первый файл создан: {0:dd.MM.yyyy HH:mm:ss}' -f $first.CreationTime) }
Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -match 'Afterburner|RivaTuner' } |
    ForEach-Object { '{0} | версия {1} | InstallDate {2}' -f $_.DisplayName, $_.DisplayVersion, $_.InstallDate }

'=== 4. Запуски (Prefetch) ==='
Get-ChildItem 'C:\Windows\Prefetch' -Filter 'MSIAFTERBURNER*' -ErrorAction SilentlyContinue |
    ForEach-Object { '{0:dd.MM.yyyy HH:mm:ss}  {1}' -f $_.LastWriteTime, $_.Name }

'=== 5. Автозапуск ==='
Get-ScheduledTask -TaskName '*Afterburner*' -ErrorAction SilentlyContinue | ForEach-Object {
    $i = Get-ScheduledTaskInfo -TaskName $_.TaskName
    ('задача {0} | {1} | последний запуск {2:dd.MM.yyyy HH:mm:ss}' -f $_.TaskName, $_.State, $i.LastRunTime)
}
foreach ($k in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run')) {
    $p = Get-ItemProperty $k -ErrorAction SilentlyContinue
    if ($p) { $p.PSObject.Properties | Where-Object { $_.Value -match 'Afterburner' } | ForEach-Object { ('автозапуск ' + $k + ': ' + $_.Name) } }
}

'=== 6. Ключи автоприменения в конфиге ==='
$cfg = Join-Path $dir 'Profiles\MSIAfterburner.cfg'
if (Test-Path $cfg) {
    $keys = Get-Content $cfg | Where-Object { $_ -match '^(StartWithWindows|ApplyOverclockingAtStartup|ApplyFanSpeedAtStartup)=' }
    if ($keys) { $keys | ForEach-Object { '   ' + $_ } } else { 'ключей автоприменения нет вовсе' }
    if (-not ($keys -match 'ApplyOverclockingAtStartup=1')) {
        '   → разгон при старте НЕ применяется: значит машина работала на стоке, даже если профиль сохранён'
    }
}

'=== 7. Что происходило рядом: установки ПО и драйверов ==='
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='MsiInstaller'} -MaxEvents 30 -ErrorAction SilentlyContinue |
    Select-Object -First 12 | ForEach-Object { '   {0:dd.MM HH:mm}  {1}' -f $_.TimeCreated, (($_.Message -split "`n")[0]) }
