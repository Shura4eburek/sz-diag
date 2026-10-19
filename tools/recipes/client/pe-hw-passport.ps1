$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Паспорт железа из WinPE (СЗ 162581): плата/BIOS/CPU/модули памяти и их фактическая частота.
#
# Грабля: inventory.ps1 в PE спотыкается на schtasks/Get-MpComputerStatus (их в PE нет) и
# до памяти с платой не доходит. Здесь — только CIM-классы, которые в PE живы, и вывод
# латиницей: консоль PE через exec-канал коверкает кириллицу.

$bb = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue
$bios = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue
$cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
$cpu = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue

Write-Output '=== BOARD / BIOS ==='
Write-Output ('Board   : ' + $bb.Manufacturer + ' ' + $bb.Product + ' rev ' + $bb.Version)
Write-Output ('BIOS    : ' + $bios.SMBIOSBIOSVersion + '  date ' + $bios.ReleaseDate)
Write-Output ('System  : ' + $cs.Manufacturer + ' ' + $cs.Model)
Write-Output ('CPU     : ' + $cpu.Name + '  (' + $cpu.NumberOfCores + 'C/' + $cpu.NumberOfLogicalProcessors + 'T)')
Write-Output ('RAM tot : ' + [math]::Round($cs.TotalPhysicalMemory / 1GB, 1) + ' GB')

Write-Output ''
Write-Output '=== MEMORY MODULES ==='
Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue |
    Select-Object BankLabel, DeviceLocator, Manufacturer, PartNumber, SerialNumber,
        @{n = 'GB'; e = { [math]::Round($_.Capacity / 1GB, 0) } },
        @{n = 'RunMHz'; e = { $_.ConfiguredClockSpeed } },
        @{n = 'SpdMHz'; e = { $_.Speed } },
        @{n = 'Volt'; e = { $_.ConfiguredVoltage } } |
    Format-Table -Auto | Out-String -Width 220

Write-Output '=== PCIe LINK (NVMe) ==='
Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
    Where-Object { $_.PNPClass -eq 'SCSIAdapter' -or $_.Name -match 'NVM' } |
    Select-Object Name, DeviceID | Format-Table -Auto | Out-String -Width 220
