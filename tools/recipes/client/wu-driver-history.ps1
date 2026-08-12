param([int]$Days = 30)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
# Что Windows Update накатил на машину и когда менялись драйверы железа.
#
# Грабля (СЗ 161346): машину разбудили 10.08 в 11:37 после 11 суток сна, и в 11:42 WU поставил
# пачку обновлений. Все последующие прогоны шли уже на ОБНОВЛЁННОЙ системе — то есть не на той,
# которую сдал клиент. Если среди обновлений был драйвер накопителя/чипсета/видео, то
# "дефект не воспроизводится" может означать "его починил апдейт", а не "железо исправно".
# Проверять ДО того, как гонять очередной прогон, и на каждой заявке, где машина полежала.
#
# Использование: szcli exec <СЗ> -f tools\recipes\client\wu-driver-history.ps1 --timeout 180

$since = (Get-Date).AddDays(-$Days)

'=== Установленные обновления (WindowsUpdateClient, Id=19 = успешно) ==='
$wu = @(Get-WinEvent -FilterHashtable @{
        LogName      = 'System'
        ProviderName = 'Microsoft-Windows-WindowsUpdateClient'
        Id           = @(19, 20)
        StartTime    = $since
    } -ErrorAction SilentlyContinue | Sort-Object TimeCreated)

if (-not $wu) { '  за период нет' }
foreach ($e in $wu) {
    $status = if ($e.Id -eq 19) { 'OK  ' } else { 'FAIL' }
    # Имя обновления — первое свойство события
    $name = ($e.Properties | Select-Object -First 1).Value
    "{0:dd.MM.yyyy HH:mm:ss}  {1} {2}" -f $e.TimeCreated, $status, $name
}

''
'=== Установка/обновление драйверов (UserPnp 20003) ==='
$pnp = @(Get-WinEvent -FilterHashtable @{
        LogName      = 'System'
        ProviderName = 'Microsoft-Windows-UserPnp'
        Id           = 20003
        StartTime    = $since
    } -ErrorAction SilentlyContinue | Sort-Object TimeCreated)

if (-not $pnp) { '  за период нет' }
foreach ($e in $pnp) {
    $x = [xml]$e.ToXml()
    $d = @{}
    foreach ($n in $x.Event.UserData.InstallDeviceID.ChildNodes) { $d[$n.Name] = $n.'#text' }
    "{0:dd.MM.yyyy HH:mm:ss}  {1} | drv={2} | {3}" -f `
        $e.TimeCreated, $d['DeviceInstanceID'], $d['DriverName'], $d['DriverProvider']
}

''
'=== Пакеты обновлений системы (Get-HotFix) ==='
Get-HotFix | Sort-Object InstalledOn -Descending | Select-Object -First 15 |
    Format-Table HotFixID, Description, InstalledOn -AutoSize | Out-String -Width 200

''
'=== Текущие драйверы ключевого железа (версия / дата / поставщик) ==='
# Классы: контроллеры накопителей, системные устройства (чипсет), видео, сеть
$classes = 'SCSIAdapter', 'HDC', 'System', 'Display', 'Net'
Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
    Where-Object { $_.DeviceClass -in $classes -and $_.DeviceName } |
    Where-Object {
        $_.DeviceName -match 'NVM|Storage|SATA|RAID|AMD|GeForce|NVIDIA|Chipset|PCI|Ethernet|Wi-?Fi'
    } |
    Sort-Object DriverDate -Descending |
    Select-Object -First 25 DeviceName, DriverVersion,
        @{n = 'DriverDate'; e = { if ($_.DriverDate) { ([datetime]$_.DriverDate).ToString('dd.MM.yyyy') } } },
        DriverProviderName |
    Format-Table -AutoSize | Out-String -Width 200
