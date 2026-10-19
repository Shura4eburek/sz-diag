$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# ОПИСЬ ПЕРЕД ПЕРЕУСТАНОВКОЙ WINDOWS: всё, что после форматирования уже не спросишь.
#
# Грабля, которая его породила (СЗ 162003, 16.09.2026): машина приехала с кастомной сборкой
# (Defender вырезан, работа под встроенным Administrator, Process Lasso с ручными правилами),
# и её решили переставить начисто. Перед этим надо письменно зафиксировать: чем машина
# активирована (OEM-ключ живёт в прошивке, но проверить это надо ДО, а не после),
# что у клиента стояло из программ, какие данные лежат в профиле и какие правки клиент
# вносил руками — иначе при выдаче нечем ответить на «а у меня было...».
#
# Вывод — простыня текста; забирать её в журнал СЗ целиком.
#   szcli exec <СЗ> -f tools\recipes\client\pre-reinstall-report.ps1 --timeout 300

'== ОС =='
$os = Get-CimInstance Win32_OperatingSystem
'{0} (build {1}), установлена {2:dd.MM.yyyy}' -f $os.Caption, $os.BuildNumber, $os.InstallDate
'редакция: ' + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
'язык интерфейса: ' + $os.MUILanguages

'== активация и ключи =='
# Ключ в прошивке (OEM) переживает переустановку — это главный ответ на «чем активировать».
$oem = (Get-CimInstance SoftwareLicensingService).OA3xOriginalProductKey
if ($oem) { "OEM-ключ в прошивке: $oem" } else { 'OEM-ключа в прошивке НЕТ — активация привязана к учётке/ключу клиента' }
Get-CimInstance SoftwareLicensingProduct -Filter "PartialProductKey is not null" |
    ForEach-Object { '   {0}: {1} (канал {2}, последние 5 ключа {3})' -f $_.Name, $(
        switch ($_.LicenseStatus) { 1 {'активирована'} 0 {'не активирована'} default {"статус $($_.LicenseStatus)"} }
    ), $_.ProductKeyChannel, $_.PartialProductKey }

'== учётные записи =='
Get-CimInstance Win32_UserAccount -Filter "LocalAccount=true" |
    ForEach-Object { '   {0} (включена: {1})' -f $_.Name, (-not $_.Disabled) }
Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    # Переменную НЕЛЬЗЯ звать $Sz: `szcli exec` подставляет номер СЗ в переменную с этим
    # именем сам, даже без явного --param (162003, 16.09.2026) — и размер профиля печатался
    # как «162003 ГБ» на всех четырёх профилях, то есть опись врала правдоподобным числом.
    $used = (Get-ChildItem $_.FullName -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1GB
    '   профиль {0,-20} {1,6:N1} ГБ' -f $_.Name, $used
}

'== установленные программы =='
# Обе ветки реестра: 32- и 64-битная, иначе половина списка теряется.
$keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
Get-ItemProperty $keys -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -and -not $_.SystemComponent } |
    Sort-Object DisplayName -Unique |
    ForEach-Object { '   {0} {1}' -f $_.DisplayName, $_.DisplayVersion }

'== игры и лаунчеры (по папкам) =='
foreach ($p in 'C:\Program Files (x86)\Steam\steamapps\common', 'C:\Program Files\Epic Games',
               'C:\Program Files (x86)\Battle.net', 'C:\XboxGames') {
    if (Test-Path $p) { "   $p"; Get-ChildItem $p -Directory -ErrorAction SilentlyContinue | ForEach-Object { "      $($_.Name)" } }
}

'== сеть =='
Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up' |
    ForEach-Object { '   {0} ({1})' -f $_.Name, $_.InterfaceDescription }
'профили Wi-Fi (пароли клиенту придётся вводить заново):'
(netsh wlan show profiles) 2>$null | Select-String 'All User Profile' | ForEach-Object { '   ' + $_.Line.Split(':')[-1].Trim() }

'== ручные правки клиента =='
$lasso = 'C:\ProgramData\ProcessLasso\config\prolasso.ini'
if (Test-Path $lasso) {
    'Process Lasso: правила есть — перенос НЕ рекомендуется, они и были частью жалобы'
    Get-Content $lasso | Select-String 'DefaultPriorities|DefaultAffinitiesEx|ProBalance|SetTimerResolution' |
        ForEach-Object { '   ' + $_.Line }
} else { 'Process Lasso: конфига нет' }
'схема питания: ' + ((powercfg /getactivescheme) -replace '.*\(|\)')

'== диски =='
Get-CimInstance Win32_DiskDrive | ForEach-Object { '   {0} {1:N0} ГБ SN {2}' -f $_.Model, ($_.Size/1GB), $_.SerialNumber.Trim() }
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' |
    ForEach-Object { '   {0} занято {1:N0} из {2:N0} ГБ, метка "{3}"' -f $_.DeviceID, (($_.Size-$_.FreeSpace)/1GB), ($_.Size/1GB), $_.VolumeName }

'== BitLocker (важно: без ключа данные после переустановки не достать) =='
$bl = Get-CimInstance -Namespace 'root\cimv2\security\microsoftvolumeencryption' -ClassName Win32_EncryptableVolume -ErrorAction SilentlyContinue
if ($bl) { $bl | ForEach-Object { '   {0}: статус защиты {1}' -f $_.DriveLetter, $_.ProtectionStatus } } else { '   не используется / класс недоступен' }
