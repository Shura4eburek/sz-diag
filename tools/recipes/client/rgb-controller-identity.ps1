# СЗ 163013: на одном и том же USB-пути контроллер раньше enumerated как композитный 048D:5711,
# а сейчас — как одиночный HID 048D:57DB. Достаём даты, чтобы понять, КОГДА он сменил личность.
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

foreach ($pid_ in '5711','57DB','5702') {
  Write-Output "=== VID_048D&PID_$pid_ на текущем хабе (8&6C00884) ==="
  $k = "HKLM:\SYSTEM\CurrentControlSet\Enum\USB\VID_048D&PID_$pid_"
  Get-ChildItem $k -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '6C00884' } | ForEach-Object {
    $id = "USB\VID_048D&PID_$pid_\$($_.PSChildName)"
    $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    "  $($_.PSChildName) | $($p.DeviceDesc -replace '^@.*;','') | Service=$($p.Service)"
    foreach ($key in 'DEVPKEY_Device_InstallDate','DEVPKEY_Device_FirstInstallDate','DEVPKEY_Device_LastArrivalDate','DEVPKEY_Device_LastRemovalDate') {
      $v = (Get-PnpDeviceProperty -InstanceId $id -KeyName $key -ErrorAction SilentlyContinue).Data
      if ($v) { $short = $key.Replace('DEVPKEY_Device_',''); "      $short = $v" }
    }
  }
}

Write-Output "=== ТЕКУЩЕЕ УСТРОЙСТВО 57DB: полный набор дат ==="
$id = (Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match 'USB\VID_048D&PID_57DB' } | Select-Object -First 1).InstanceId
"instance: $id"
foreach ($key in 'DEVPKEY_Device_InstallDate','DEVPKEY_Device_FirstInstallDate','DEVPKEY_Device_LastArrivalDate') {
  "   $key = $((Get-PnpDeviceProperty -InstanceId $id -KeyName $key -ErrorAction SilentlyContinue).Data)"
}

Write-Output "=== КОГДА СТАВИЛСЯ ТЕКУЩИЙ BIOS / ПОСЛЕДНИЕ ОБНОВЛЕНИЯ ==="
Get-CimInstance Win32_BIOS | Select-Object SMBIOSBIOSVersion,ReleaseDate | Format-List
Write-Output "-- дата установки Windows --"
(Get-CimInstance Win32_OperatingSystem).InstallDate
