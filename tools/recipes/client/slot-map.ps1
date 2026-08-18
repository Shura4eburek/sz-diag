$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# БЫСТРАЯ карта «накопитель -> номер шины PCI (слот)» + серийник + имя в журнале.
#
# Грабля (СЗ 161346, 18.08): `pcie-topology.ps1` даёт ту же карту, но не укладывается
# в 120 с exec'а — его топят сырой перебор PID 1..14 у каждого контроллера и обход
# всего `Win32_PnPEntity`. А перед каждым прогоном нужно ровно одно: убедиться, какой
# диск сейчас в каком слоте (иначе весь тест интерпретируется вслепую).
# Здесь только это, без спецификаций линии — на живой машине отвечает за пару секунд.

$dd = @(Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue)
$disks = @(Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object BusType -eq 'NVMe' | Sort-Object DeviceId)
if (-not $disks) { 'NVMe diskov ne naydeno.'; return }

foreach ($d in $disks) {
    $wmi = $dd | Where-Object { $_.Index -eq $d.DeviceId } | Select-Object -First 1
    $bus = '?'
    if ($wmi -and $wmi.PNPDeviceID) {
        $dev = Get-PnpDevice -InstanceId $wmi.PNPDeviceID -ErrorAction SilentlyContinue
        if ($dev) {
            $parentId = (Get-PnpDeviceProperty -InstanceId $dev.InstanceId -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
            if ($parentId) {
                $loc = (Get-PnpDeviceProperty -InstanceId $parentId -KeyName 'DEVPKEY_Device_LocationInfo' -ErrorAction SilentlyContinue).Data
                if ($loc -match 'PCI bus (\d+)') { $bus = $Matches[1] }
            }
        }
    }
    # Букву тома тянем, чтобы сразу видеть, какой диск системный.
    # Грабля: у CimInstance НЕТ метода GetRelated (это метод старого WMI-объекта из Get-WmiObject) —
    # связанные классы обходятся через Get-CimAssociatedInstance, иначе рецепт сыплет MethodNotFound.
    $letters = @(
        if ($wmi) {
            Get-CimAssociatedInstance -InputObject $wmi -ResultClassName Win32_DiskPartition -ErrorAction SilentlyContinue |
                ForEach-Object { Get-CimAssociatedInstance -InputObject $_ -ResultClassName Win32_LogicalDisk -ErrorAction SilentlyContinue } |
                ForEach-Object { $_.DeviceID }
        }
    ) -join ','

    [PSCustomObject]@{
        Drive    = "PhysicalDrive$($d.DeviceId)"
        Model    = $d.FriendlyName
        Serial   = $d.SerialNumber
        Firmware = $d.FirmwareVersion
        SizeGB   = [math]::Round($d.Size / 1GB)
        PciBus   = $bus
        Volumes  = $letters
        InLog    = "\Device\Harddisk$($d.DeviceId)\DR$($d.DeviceId)"
    }
}

''
'=== Partition/Diagnostic 1006 (последние записи: чем система видела диски при старте) ==='
Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Partition/Diagnostic'; Id = 1006 } -MaxEvents 12 -ErrorAction SilentlyContinue |
    ForEach-Object {
        $x = [xml]$_.ToXml()
        # Поле шины в 1006 зовётся `Bus` (не `BusNumber`) — с неверным именем колонка молча пустая.
        $d = @{}
        foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
        '{0:dd.MM HH:mm:ss}  Disk{1}  adapter={2} bus={3}  {4}  SN={5}' -f `
            $_.TimeCreated, $d['DiskNumber'], $d['Adapter'], $d['Bus'], $d['Model'], $d['SerialNumber']
    }
