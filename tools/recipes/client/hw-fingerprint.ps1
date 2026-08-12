$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Паспорт железа: что и в каком слоте стоит. Снимок кладётся в JSON, при повторном запуске
# печатается ДИФФ с предыдущим снимком.
#
# Грабля (СЗ 161346, бэклог п.136): пока машина ждала диагностики, кто-то (шоурум?) поменял
# два NVMe местами — 29.07 между 12:51 и 13:57. Мы этого не знали, привязали архивные ошибки
# журнала по текущей карте (получили зеркальный вывод, ушедший клиенту) и полтора дня гоняли
# прогоны в раскладке, В КОТОРОЙ ДЕФЕКТ НИКОГДА НЕ ПРОЯВЛЯЛСЯ. Условия воспроизведения были
# испорчены до нас, и узнали мы об этом случайно.
#
# Гонять ПЕРВЫМ ДЕЛОМ на новой СЗ и повторно перед каждым прогоном.
# Использование: szcli exec <СЗ> -f tools\recipes\client\hw-fingerprint.ps1 --timeout 180

$dir = 'C:\ProgramData\szdiag\hw'
New-Item -ItemType Directory -Path $dir -Force -ErrorAction SilentlyContinue | Out-Null

function Val($o) { if ($null -eq $o) { '' } else { ($o -as [string]).Trim() } }

$snap = [ordered]@{}
$snap.Taken = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')

# --- Плата и BIOS: смена версии BIOS = кто-то шил или сбрасывал ---
$bb = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue
$bs = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue
$snap.Board = "$(Val $bb.Manufacturer) $(Val $bb.Product) SN=$(Val $bb.SerialNumber)"
$snap.Bios  = "$(Val $bs.SMBIOSBIOSVersion) от $(if ($bs.ReleaseDate) { ([datetime]$bs.ReleaseDate).ToString('dd.MM.yyyy') })"

$cpu = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1
$snap.Cpu = "$(Val $cpu.Name) SN=$(Val $cpu.ProcessorId)"

# --- Память: слот + серийник + партномер + фактическая частота (виден профиль XMP/EXPO) ---
$snap.Memory = [ordered]@{}
foreach ($m in (Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue | Sort-Object DeviceLocator)) {
    $snap.Memory[(Val $m.DeviceLocator)] =
        "$([math]::Round($m.Capacity/1GB)) ГБ $(Val $m.PartNumber) SN=$(Val $m.SerialNumber) @ $($m.ConfiguredClockSpeed) (JEDEC $($m.Speed))"
}

# --- Накопители: ключевое — В КАКОМ СЛОТЕ. Слот = PCI bus/device/function, он и определяет
#     номер \Device\HarddiskN и \Device\RaidPortN в журнале ---
$snap.Disks = [ordered]@{}
foreach ($d in (Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue | Sort-Object Index)) {
    $pnp = Get-CimInstance Win32_PnPEntity -Filter "DeviceID='$($d.PNPDeviceID -replace '\\','\\')'" -ErrorAction SilentlyContinue
    $loc = ''
    if ($pnp) {
        $loc = Val (Get-CimInstance -ClassName Win32_PnPEntity -ErrorAction SilentlyContinue |
                    Where-Object { $_.DeviceID -eq $d.PNPDeviceID } |
                    ForEach-Object { (Get-CimAssociatedInstance $_ -ResultClassName Win32_PnPDevice -ErrorAction SilentlyContinue) })
    }
    $key = "$(Val $d.Model) SN=$(Val $d.SerialNumber)"
    $snap.Disks[$key] = "Index=$($d.Index) SCSIPort=$($d.SCSIPort) $([math]::Round($d.Size/1GB)) ГБ"
}

# Слот берём из журнала Partition/Diagnostic — там есть Bus/Device/Function (аппаратный роз'єм)
$last = @{}
Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Partition/Diagnostic'; Id=1006} -MaxEvents 200 -ErrorAction SilentlyContinue |
    Sort-Object TimeCreated | ForEach-Object {
        $x = [xml]$_.ToXml(); $p = @{}
        foreach ($n in $x.Event.EventData.Data) { $p[$n.Name] = $n.'#text' }
        if ($p['SerialNumber']) {
            $last[$p['SerialNumber'].Trim()] =
                "PCI bus $($p['Bus']) dev $($p['Device']) fn $($p['Function']) | Disk $($p['DiskNumber']) | RaidPort $($p['Adapter'])"
        }
    }
$snap.DiskSlots = [ordered]@{}
foreach ($k in $last.Keys) { $snap.DiskSlots[$k] = $last[$k] }

# --- Видео и сеть ---
$snap.Gpu = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
              ForEach-Object { "$(Val $_.Name) drv=$(Val $_.DriverVersion) $(Val $_.PNPDeviceID)" }) -join ' ; '
$snap.Net = @(Get-CimInstance Win32_NetworkAdapter -Filter 'PhysicalAdapter=TRUE' -ErrorAction SilentlyContinue |
              ForEach-Object { "$(Val $_.Name) MAC=$(Val $_.MACAddress)" }) -join ' ; '

# --- Печать снимка ---
'=== ПАСПОРТ ЖЕЛЕЗА на ' + $snap.Taken + ' ==='
"Плата : $($snap.Board)"
"BIOS  : $($snap.Bios)"
"CPU   : $($snap.Cpu)"
'Память:'
foreach ($k in $snap.Memory.Keys) { "  $k = $($snap.Memory[$k])" }
'Диски :'
foreach ($k in $snap.Disks.Keys) { "  $k -> $($snap.Disks[$k])" }
'Слоты накопителей (по журналу, последнее известное):'
foreach ($k in $snap.DiskSlots.Keys) { "  SN=$k -> $($snap.DiskSlots[$k])" }
"GPU   : $($snap.Gpu)"
"Сеть  : $($snap.Net)"

# --- Дифф с предыдущим снимком ---
$prevFile = Get-ChildItem $dir -Filter '*.json' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($prevFile) {
    $prev = Get-Content $prevFile.FullName -Raw | ConvertFrom-Json
    ''
    "=== ИЗМЕНЕНИЯ с $($prev.Taken) ==="
    $diff = 0
    foreach ($field in 'Board', 'Bios', 'Cpu', 'Gpu', 'Net') {
        if ($prev.$field -ne $snap[$field]) {
            $diff++
            "  !!! $field"
            "      было : $($prev.$field)"
            "      стало: $($snap[$field])"
        }
    }
    foreach ($section in 'Memory', 'Disks', 'DiskSlots') {
        $old = @{}; if ($prev.$section) { $prev.$section.PSObject.Properties | ForEach-Object { $old[$_.Name] = $_.Value } }
        $new = $snap[$section]
        foreach ($k in $new.Keys) {
            if (-not $old.ContainsKey($k)) { $diff++; "  !!! $section : ПОЯВИЛОСЬ  $k = $($new[$k])" }
            elseif ($old[$k] -ne $new[$k]) {
                $diff++
                "  !!! $section : $k"
                "      было : $($old[$k])"
                "      стало: $($new[$k])"
            }
        }
        foreach ($k in $old.Keys) {
            if (-not $new.Contains($k)) { $diff++; "  !!! $section : ПРОПАЛО   $k = $($old[$k])" }
        }
    }
    if ($diff -eq 0) { '  изменений нет' }
    else { "  ВСЕГО РАСХОЖДЕНИЙ: $diff — железо трогали, прогоны идут не в исходной конфигурации" }
}
else { ''; '(предыдущего снимка нет — это первый, дальше будет с чем сравнивать)' }

$out = Join-Path $dir ((Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
$snap | ConvertTo-Json -Depth 5 | Set-Content $out -Encoding UTF8
''
"снимок сохранён: $out"
