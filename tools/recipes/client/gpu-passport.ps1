$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Паспорт видеокарты для отправки в АСЦ: точная модель, subsystem ID, версия vBIOS, драйвер,
# ширина/скорость шины PCIe. Всё, что сервисный центр спрашивает в заявке на ремонт, — одним
# запуском, чтобы не лазить по диспетчеру устройств руками.
#
# Грабля (СЗ 160705, 12.08.2026): при отправке карты в АСЦ понадобились subsystem ID и версия
# BIOS — их нет ни в одной секции `diag`, а машина к тому моменту уже была под прогоном.
#   szcli exec <СЗ> -f tools\recipes\client\gpu-passport.ps1

'=== Видеокарта ==='
Get-CimInstance Win32_VideoController | ForEach-Object {
    ('Название           : ' + $_.Name)
    ('PNPDeviceID        : ' + $_.PNPDeviceID)
    ('Версия драйвера    : ' + $_.DriverVersion + '  (от ' + ($_.DriverDate) + ')')
    ('Видеопамять, ГБ    : ' + [math]::Round($_.AdapterRAM / 1GB, 1) + '   (значение врёт на картах > 4 ГБ — сверяться с моделью)')
    ('vBIOS              : ' + $_.VideoProcessor + ' / ' + $_.AdapterCompatibility)
    ('Статус             : ' + $_.Status + ', ошибка конфигурации: ' + $_.ConfigManagerErrorCode)
    ''
}

'=== Реестр драйвера: точная плата и BIOS ==='
# HardwareInformation.* лежат как REG_BINARY с ASCII-строкой внутри: без декодирования
# получишь простыню трёхзначных чисел (проверено на 160705 — "049049053045..." вместо
# part number BIOS "115-D754BP0-101").
function Convert-HwString($v) {
    if ($null -eq $v) { return $null }
    if ($v -is [string]) { return $v }
    ((($v | ForEach-Object { [char][int]$_ }) -join '') -replace "`0", '').Trim()
}
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if ($p.DriverDesc) {
            ('DriverDesc         : ' + $p.DriverDesc)
            foreach ($k in @('AdapterString','BiosString','ChipType','DacType','MemorySize')) {
                $val = Convert-HwString $p."HardwareInformation.$k"
                if ($val) { ('{0,-18} : {1}' -f $k, $val) }
            }
            if ($p.MatchingDeviceId) { ('MatchingDeviceId   : ' + $p.MatchingDeviceId) }
            ''
        }
    }

'=== PCIe: слот, ширина, скорость ==='
Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | ForEach-Object {
    $d = $_
    ('Устройство         : ' + $d.FriendlyName + '  [' + $d.Status + ']')
    ('InstanceId         : ' + $d.InstanceId)
    foreach ($k in @('DEVPKEY_PciDevice_CurrentLinkSpeed','DEVPKEY_PciDevice_CurrentLinkWidth','DEVPKEY_PciDevice_MaxLinkSpeed','DEVPKEY_PciDevice_MaxLinkWidth','DEVPKEY_Device_LocationInfo')) {
        $v = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName $k -ErrorAction SilentlyContinue).Data
        if ($null -ne $v) { ('{0,-18} : {1}' -f ($k -replace 'DEVPKEY_(PciDevice_|Device_)',''), $v) }
    }
    ''
}

'=== Ошибки видеодрайвера в журнале (TDR и падения) ==='
$ev = Get-WinEvent -FilterHashtable @{LogName='System'; Id=4101,4098,14,13} -MaxEvents 40 -ErrorAction SilentlyContinue |
      Where-Object { $_.ProviderName -match 'Display|amdkmdap|nvlddmkm|amdwddmg' }
if ($ev) { $ev | Select-Object -First 10 TimeCreated,Id,ProviderName | Format-Table -Auto | Out-String -Width 120 }
else { 'событий TDR/падений видеодрайвера нет' }
