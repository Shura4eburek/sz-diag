# Рецепт: из WinPE найти установленную Windows на дисках клиента и показать минидампы.
# Грабля: в PE буква системного тома клиента не C: (в PE C: — это X:\... ram-диск),
# поэтому перебираем все тома и ищем \Windows\System32\ntoskrnl.exe.
# Время событий/файлов из PE идёт в зоне PE — сверять таймлайн только по UTC.
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$found = @()
foreach ($v in (Get-Volume | Where-Object { $_.DriveLetter })) {
    $root = "$($v.DriveLetter):"
    if (Test-Path "$root\Windows\System32\ntoskrnl.exe") {
        $found += $root
    }
}

if ($found.Count -eq 0) {
    Write-Output 'WINDOWS_NOT_FOUND: ни на одном томе нет \Windows\System32\ntoskrnl.exe'
    Get-Volume | Where-Object { $_.DriveLetter } |
        Select-Object DriveLetter, FileSystemLabel, FileSystem,
            @{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}} |
        Format-Table -AutoSize | Out-String -Width 200
    return
}

foreach ($root in $found) {
    Write-Output "=== Windows на $root"

    $mini = "$root\Windows\Minidump"
    if (Test-Path $mini) {
        Write-Output "--- минидампы ($mini):"
        Get-ChildItem $mini -Filter *.dmp -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object Name,
                @{n='UTC';e={$_.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss')}},
                @{n='KB';e={[math]::Round($_.Length/1KB)}} |
            Format-Table -AutoSize | Out-String -Width 200
    } else {
        Write-Output "--- минидампов нет: каталог $mini отсутствует"
    }

    $full = "$root\Windows\MEMORY.DMP"
    if (Test-Path $full) {
        $f = Get-Item $full
        Write-Output ("--- MEMORY.DMP: {0} МБ, UTC {1}" -f
            [math]::Round($f.Length/1MB,1), $f.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss'))
    } else {
        Write-Output '--- MEMORY.DMP отсутствует'
    }

    $log = "$root\Windows\System32\winevt\Logs"
    if (Test-Path $log) {
        Write-Output '--- журналы событий:'
        Get-ChildItem $log -Filter *.evtx -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'System|Application|Kernel-WHEA|Kernel-Power' } |
            Select-Object Name,
                @{n='UTC';e={$_.LastWriteTimeUtc.ToString('yyyy-MM-dd HH:mm:ss')}},
                @{n='MB';e={[math]::Round($_.Length/1MB,1)}} |
            Format-Table -AutoSize | Out-String -Width 200
    }
}
