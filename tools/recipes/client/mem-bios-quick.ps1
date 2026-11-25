# Швидко: частота/профіль пам'яті, версія BIOS, плата — для звірки з відомими кейсами EXPO на AM5.
# Inline exec через szcli.cmd губить лапки (СЗ 169768) — запускати через -f.
Get-CimInstance Win32_PhysicalMemory | Select-Object BankLabel,DeviceLocator,PartNumber,SerialNumber,Capacity,Speed,ConfiguredClockSpeed | Format-Table -AutoSize | Out-String -Width 200
Get-CimInstance Win32_BIOS | Select-Object SMBIOSBIOSVersion,ReleaseDate | Format-Table -AutoSize | Out-String
Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer,Product,SerialNumber | Format-Table -AutoSize | Out-String
Get-CimInstance Win32_VideoController | Select-Object Name,PNPDeviceID,Status | Format-Table -AutoSize | Out-String -Width 200
