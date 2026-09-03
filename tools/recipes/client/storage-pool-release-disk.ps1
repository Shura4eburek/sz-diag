# Рецепт: вернуть диск из пула Storage Spaces обычному дисковому стеку и нарезать GPT.
# Породила грабля: 111111 — HDD 1 ТБ виден в диспетчере устройств, но отсутствует в
# "Управлении дисками"/diskpart, т.к. состоит в пустом пуле (CannotPoolReason = In a Pool).
# ВНИМАНИЕ: сносит пул. Запускать, только убедившись, что виртуальных дисков в нём нет.
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$serial = 'WD-WCC6Y2KCEV7V'   # серийник целевого диска

$pools = Get-StoragePool | Where-Object { -not $_.IsPrimordial }
foreach ($pool in $pools) {
    $vds = @($pool | Get-VirtualDisk -ErrorAction SilentlyContinue)
    if ($vds.Count -gt 0) {
        Write-Output "ОТМЕНА: в пуле '$($pool.FriendlyName)' есть виртуальные диски ($($vds.Count)) — руками"
        exit 1
    }
    Write-Output "Сношу пул '$($pool.FriendlyName)'"
    $pool | Remove-StoragePool -Confirm:$false
}

Start-Sleep -Seconds 3
$disk = Get-Disk | Where-Object { $_.SerialNumber -like "*$serial*" }
if (-not $disk) { Write-Output 'ОШИБКА: диск так и не появился в Get-Disk'; exit 2 }
Write-Output "Диск №$($disk.Number): $($disk.FriendlyName), $([math]::Round($disk.Size/1GB,1)) ГБ, стиль=$($disk.PartitionStyle), offline=$($disk.IsOffline)"

if ($disk.IsOffline)  { $disk | Set-Disk -IsOffline $false }
if ($disk.IsReadOnly) { $disk | Set-Disk -IsReadOnly $false }
$disk = Get-Disk -Number $disk.Number

if ($disk.PartitionStyle -eq 'RAW') {
    Initialize-Disk -Number $disk.Number -PartitionStyle GPT
} elseif ($disk.PartitionStyle -ne 'GPT') {
    Clear-Disk -Number $disk.Number -RemoveData -RemoveOEM -Confirm:$false
    Initialize-Disk -Number $disk.Number -PartitionStyle GPT
}

$part = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
$vol  = Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel 'DATA' -Confirm:$false

Write-Output '=== Результат ==='
Get-Disk -Number $disk.Number | Format-Table Number, FriendlyName, Size, PartitionStyle, OperationalStatus -AutoSize | Out-String -Width 200
Get-Volume -DriveLetter $part.DriveLetter | Format-Table DriveLetter, FileSystemLabel, FileSystem, Size, SizeRemaining, HealthStatus -AutoSize | Out-String -Width 200
