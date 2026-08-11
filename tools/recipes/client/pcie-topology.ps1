$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Топология NVMe: в каком разъёме сидит диск, чем подключён (CPU или чипсет), и на какой
# скорости/ширине реально работает линия PCIe.
#
# Грабля (СЗ 161346): клиент попросил зафиксировать разъёмы M.2 и ширину линий — на AM5 один
# M.2 висит на процессоре, другой на чипсете, и в части конфигураций линии делятся с
# видеокартой. Ответить «посмотрите в BIOS» нельзя: машина у клиента, доступ только сетевой.
# Фактическая ширина/скорость линии — это и есть результат конфигурации, и её видно из ОС.
#
# Заодно чинит вторую боль: привязку `\Device\RaidPortN` и `\Device\HarddiskN` к конкретному
# диску. Один и тот же диск в журнале зовут тремя разными именами, и без карты выводы о том,
# «какой накопитель сыпется», строятся на догадке.

# DEVPKEY_PciDevice_* — их отдаёт сам PCI-стек, гадать по названиям не нужно.
# ВНИМАНИЕ на номера свойств: ссылки на «PID 1..4» неверны — под ними лежат DeviceType,
# CurrentSpeedAndMode, BaseClass, SubClass, и в отчёте это выглядело как MaxLinkSpeed Gen1
# при CurrentLinkSpeed Gen2 и ширина x8 у NVMe (СЗ 161346). Линия описывается PID 9..12.
$PCI = '{3ab22e31-8264-4b4e-9af5-a8d2d8e33e62}'
$props = [ordered]@{
    "$PCI 9"  = 'CurrentLinkSpeed'; "$PCI 10" = 'CurrentLinkWidth'
    "$PCI 11" = 'MaxLinkSpeed';     "$PCI 12" = 'MaxLinkWidth'
}
# Если раскладка в этой сборке Windows иная — печатаем сырой перебор, чтобы не гадать снова.
$DumpRaw = $true
# Кодировка скорости у PCI: 1=2.5 (Gen1), 2=5.0 (Gen2), 3=8.0 (Gen3), 4=16 (Gen4), 5=32 (Gen5) ГТ/с.
$genOf = @{ 1 = 'Gen1 (2.5 GT/s)'; 2 = 'Gen2 (5 GT/s)'; 3 = 'Gen3 (8 GT/s)'; 4 = 'Gen4 (16 GT/s)'; 5 = 'Gen5 (32 GT/s)' }

'=== Накопители: диск -> контроллер -> линия PCIe ==='
$disks = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object BusType -eq 'NVMe' | Sort-Object DeviceId
foreach ($d in $disks) {
    $wmi = Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue |
        Where-Object { $_.Index -eq $d.DeviceId } | Select-Object -First 1
    ''
    "--- PhysicalDrive$($d.DeviceId): $($d.FriendlyName) [$($d.SerialNumber)]"
    "    В журнале зовётся: \Device\Harddisk$($d.DeviceId)\DR$($d.DeviceId)   SCSIPort=$($wmi.SCSIPort) (-> \Device\RaidPort$($wmi.SCSIPort))"

    # От диска поднимаемся к его NVMe-контроллеру: именно у контроллера лежат свойства линии.
    $ctrl = $null
    if ($wmi -and $wmi.PNPDeviceID) {
        $disk = Get-PnpDevice -InstanceId $wmi.PNPDeviceID -ErrorAction SilentlyContinue
        if ($disk) {
            $parentId = (Get-PnpDeviceProperty -InstanceId $disk.InstanceId -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
            if ($parentId) { $ctrl = Get-PnpDevice -InstanceId $parentId -ErrorAction SilentlyContinue }
        }
    }
    if (-not $ctrl) { "    контроллер не определён"; continue }

    "    Контроллер: $($ctrl.FriendlyName)"
    $loc = (Get-PnpDeviceProperty -InstanceId $ctrl.InstanceId -KeyName 'DEVPKEY_Device_LocationInfo' -ErrorAction SilentlyContinue).Data
    "    Расположение: $loc"

    foreach ($k in $props.Keys | Sort-Object) {
        $v = (Get-PnpDeviceProperty -InstanceId $ctrl.InstanceId -KeyName $k -ErrorAction SilentlyContinue).Data
        if ($null -ne $v) {
            $name = $props[$k]
            $text = if ($name -like '*Speed*') { if ($genOf.ContainsKey([int]$v)) { $genOf[[int]$v] } else { "код $v" } } else { "x$v" }
            "    {0,-17} {1}" -f $name, $text
        }
    }

    # Шина PCI: на AM5 линии процессора отдают низкие номера шин, чипсетные — заметно выше.
    # Точную принадлежность утверждать по номеру нельзя, поэтому печатаем факт, а вывод — рядом.
    if ($loc -match 'PCI bus (\d+)') { "    Номер шины PCI: $($Matches[1])" }

    if ($DumpRaw) {
        '    сырой перебор свойств PCI (PID -> значение):'
        foreach ($pid_ in 1..14) {
            $v = (Get-PnpDeviceProperty -InstanceId $ctrl.InstanceId -KeyName "$PCI $pid_" -ErrorAction SilentlyContinue).Data
            if ($null -ne $v) { "       PID {0,-3} = {1}" -f $pid_, $v }
        }
    }
}

''
'=== Все PCIe-мосты и что за ними (для понимания, чьи это линии) ==='
Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
    Where-Object { $_.PNPDeviceID -like 'PCI\*' -and ($_.Name -match 'NVM|PCI Express|Standard NVM|Root Port|Видеоадаптер|Display') } |
    Select-Object -First 25 Name, DeviceID |
    ForEach-Object { "   $($_.Name)" }

''
'=== Видеокарта: её линия PCIe (делится ли с M.2) ==='
$gpus = Get-PnpDevice -Class Display -Status OK -ErrorAction SilentlyContinue
foreach ($g in $gpus) {
    "--- $($g.FriendlyName)"
    $loc = (Get-PnpDeviceProperty -InstanceId $g.InstanceId -KeyName 'DEVPKEY_Device_LocationInfo' -ErrorAction SilentlyContinue).Data
    "    Расположение: $loc"
    foreach ($k in $props.Keys | Sort-Object) {
        $v = (Get-PnpDeviceProperty -InstanceId $g.InstanceId -KeyName $k -ErrorAction SilentlyContinue).Data
        if ($null -ne $v) {
            $name = $props[$k]
            $text = if ($name -like '*Speed*') { if ($genOf.ContainsKey([int]$v)) { $genOf[[int]$v] } else { "код $v" } } else { "x$v" }
            "    {0,-17} {1}" -f $name, $text
        }
    }
}

''
'=== Материнская плата / BIOS ==='
$bb = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue
$bios = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue
"   Плата: $($bb.Manufacturer) $($bb.Product)"
"   BIOS:  $($bios.SMBIOSBIOSVersion) от $($bios.ReleaseDate)"
