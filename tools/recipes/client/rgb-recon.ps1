# Разведка по RGB-подсветке на плате (грабля: SignalRGB "driver outdated",
# ARGB-хедеры Gigabyte не управляются — надо понять, виден ли ITE-контроллер).
# СЗ 163013: Gigabyte X870 GAMING X WIFI7.
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

Write-Output "=== ПЛАТА / BIOS ==="
Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer,Product,Version | Format-List
Get-CimInstance Win32_BIOS | Select-Object Manufacturer,SMBIOSBIOSVersion,ReleaseDate | Format-List

Write-Output "=== USB/HID УСТРОЙСТВА ITE (VID_048D) + прочие RGB-кандидаты ==="
Get-PnpDevice -PresentOnly | Where-Object {
  $_.InstanceId -match 'VID_048D' -or $_.FriendlyName -match 'ITE|RGB|LED|Fusion|AURA|Mystic'
} | Select-Object Status,Class,FriendlyName,InstanceId | Format-List

Write-Output "=== ВСЕ HID-устройства (сырьё, вдруг контроллер под другим VID) ==="
Get-PnpDevice -PresentOnly -Class HIDClass | Select-Object Status,FriendlyName,InstanceId | Format-Table -AutoSize | Out-String -Width 200

Write-Output "=== ПРОБЛЕМНЫЕ УСТРОЙСТВА (код ошибки != 0) ==="
Get-PnpDevice | Where-Object { $_.Status -ne 'OK' -and $_.Status -ne 'Unknown' } | Select-Object Status,Class,FriendlyName,InstanceId | Format-Table -AutoSize | Out-String -Width 200

Write-Output "=== СОФТ ПОДСВЕТКИ ==="
$keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
Get-ItemProperty $keys -ErrorAction SilentlyContinue |
  Where-Object { $_.DisplayName -match 'SignalRGB|GIGABYTE|RGB|Fusion|AORUS|Control Center|OpenRGB|Armoury|iCUE|Mystic' } |
  Select-Object DisplayName,DisplayVersion,Publisher | Sort-Object DisplayName | Format-Table -AutoSize | Out-String -Width 200

Write-Output "=== СЛУЖБЫ/ПРОЦЕССЫ ПОДСВЕТКИ ==="
Get-Service | Where-Object { $_.Name -match 'Signal|RGB|GIGABYTE|Fusion|LightingService|GCC|AORUS' } | Select-Object Status,StartType,Name,DisplayName | Format-Table -AutoSize | Out-String -Width 200
Get-Process | Where-Object { $_.Name -match 'Signal|RGB|Fusion|GCC|LightingService' } | Select-Object Name,Id,Path | Format-Table -AutoSize | Out-String -Width 200

Write-Output "=== ДРАЙВЕРЫ-СЫРЬЁ (ITE / RGB service drivers) ==="
Get-CimInstance Win32_SystemDriver | Where-Object { $_.Name -match 'ite|rgb|signal|gigabyte|Gv64|SignalRgb|WinRing|inpout' } |
  Select-Object State,Name,PathName | Format-Table -AutoSize | Out-String -Width 200
