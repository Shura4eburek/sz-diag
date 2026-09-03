# Рецепт: диск виден в диспетчере устройств, но отсутствует в "Управлении дисками"/diskpart.
# Породила грабля: 111111 — HDD 1 ТБ виден как устройство, но storage stack его не отдаёт.
# Снимает срез со всех слоёв: PnP -> Win32_DiskDrive -> Get-Disk -> PhysicalDisk -> тома.
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

Write-Output '=== PnP: DiskDrive / HDC / SCSIAdapter ==='
Get-PnpDevice | Where-Object { $_.Class -in @('DiskDrive','HDC','SCSIAdapter','Volume') } |
    Select-Object Status, Class, Problem, FriendlyName, InstanceId |
    Format-Table -AutoSize | Out-String -Width 300

Write-Output '=== Win32_DiskDrive (WMI) ==='
Get-CimInstance Win32_DiskDrive |
    Select-Object Index, Model, SerialNumber, Size, InterfaceType, MediaType, Status, Partitions, PNPDeviceID |
    Format-List | Out-String -Width 300

Write-Output '=== Get-Disk (storage stack) ==='
Get-Disk | Format-Table Number, FriendlyName, SerialNumber, Size, PartitionStyle, OperationalStatus, HealthStatus, IsOffline, IsReadOnly, BusType -AutoSize |
    Out-String -Width 300

Write-Output '=== Get-PhysicalDisk ==='
Get-PhysicalDisk |
    Format-Table DeviceId, FriendlyName, SerialNumber, MediaType, BusType, Size, CanPool, CannotPoolReason, OperationalStatus, HealthStatus -AutoSize |
    Out-String -Width 300

Write-Output '=== Storage pools (Storage Spaces) ==='
try { Get-StoragePool -ErrorAction Stop | Format-Table FriendlyName, IsPrimordial, OperationalStatus, HealthStatus, Size -AutoSize | Out-String -Width 200 } catch { "нет: $_" }

Write-Output '=== Тома ==='
Get-Volume | Format-Table DriveLetter, FileSystemLabel, FileSystem, Size, SizeRemaining, HealthStatus -AutoSize | Out-String -Width 200

Write-Output '=== SAN policy ==='
'san' | diskpart | Out-String

Write-Output '=== Службы дисковых стеков ==='
Get-Service -Name partmgr, disk, volmgr, volsnap, vds, storahci, stornvme, iaStorAC, iaStorVD -ErrorAction SilentlyContinue |
    Format-Table Name, Status, StartType -AutoSize | Out-String -Width 120
