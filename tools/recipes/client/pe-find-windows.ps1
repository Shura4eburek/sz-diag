$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Обмеження WinPE (нема Get-PnpDevice, Get-StorageReliabilityCounter частково пустий тощо) -
# зведений список у шапці pe-offline-triage.ps1 (бэклог п.192).
#
# Грабля 162938: offline-mount-windows.ps1 хардкодит "диск 0, партиция 3" - на другой
# машине раскладка другая (NVMe единственный, но номер партиции плавает; бывает второй
# диск с чужой виндой). Этот рецепт СНАЧАЛА показывает раскладку и сам находит том,
# где реально лежит \Windows\System32\config\SYSTEM, и вешает на него букву W:.
'=== диски ==='
foreach ($d in (Get-Disk | Sort-Object Number)) {
    ('  диск {0}: {1}  {2} GB  {3}  стиль={4}  здоровье={5}' -f $d.Number, $d.FriendlyName,
        [int]($d.Size / 1GB), $d.BusType, $d.PartitionStyle, $d.HealthStatus)
}
''
'=== разделы / тома ==='
foreach ($p in (Get-Partition | Sort-Object DiskNumber, PartitionNumber)) {
    $v = Get-Volume -Partition $p -ErrorAction SilentlyContinue
    ('  d{0} p{1}  {2,7} MB  тип={3,-16} буква={4}  fs={5}  метка={6}' -f $p.DiskNumber,
        $p.PartitionNumber, [int]($p.Size / 1MB), $p.Type, $p.DriveLetter, $v.FileSystemType, $v.FileSystemLabel)
}
''
'=== поиск установленной Windows ==='
$found = $null
foreach ($p in (Get-Partition | Where-Object { $_.Size -gt 10GB } | Sort-Object DiskNumber, PartitionNumber)) {
    $letter = $p.DriveLetter
    $temp = $false
    if (-not $letter) {
        foreach ($cand in 'W', 'Y', 'Z', 'V', 'U') {
            if (-not (Test-Path ($cand + ':\'))) {
                try { Set-Partition -InputObject $p -NewDriveLetter $cand -ErrorAction Stop; $letter = $cand; $temp = $true; break } catch { }
            }
        }
    }
    if (-not $letter) { continue }
    $root = $letter + ':'
    $hive = $root + '\Windows\System32\config\SYSTEM'
    if (Test-Path $hive) {
        ('  Windows найдена: диск {0} партиция {1} -> {2}' -f $p.DiskNumber, $p.PartitionNumber, $root)
        $found = $p
        $win = Get-Item ($root + '\Windows')
        ('  \Windows создана : ' + $win.CreationTime)
        ('  SYSTEM hive изм. : ' + (Get-Item $hive).LastWriteTime)
        $v = Get-Volume -Partition $p -ErrorAction SilentlyContinue
        ('  свободно         : ' + [int]($v.SizeRemaining / 1GB) + ' GB из ' + [int]($v.Size / 1GB) + ' GB')
        break
    }
    elseif ($temp) {
        # чужой раздел - букву не оставляем
        try { Remove-PartitionAccessPath -InputObject $p -AccessPath ($root + '\') -ErrorAction Stop } catch { }
    }
}
if (-not $found) { '  установленная Windows не найдена ни на одном разделе' }
else {
    # приводим к привычной W:, чтобы остальные offline-рецепты работали как есть
    if ($found.DriveLetter -ne 'W' -and -not (Test-Path 'W:\')) {
        try { Set-Partition -InputObject $found -NewDriveLetter W -ErrorAction Stop; '  переназначена на W:' } catch { ('  на W: не встала: ' + $_.Exception.Message) }
    }
}
