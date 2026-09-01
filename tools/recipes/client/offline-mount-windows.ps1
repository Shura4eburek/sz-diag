$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Раздел винды клиента (диск 0, партиция 3) в PE поднялся без буквы - вешаем W:
$p = Get-Partition -DiskNumber 0 -PartitionNumber 3
if (-not $p.DriveLetter) {
    try { Set-Partition -DiskNumber 0 -PartitionNumber 3 -NewDriveLetter W -ErrorAction Stop; 'буква W: назначена' }
    catch {
        ('Set-Partition: ' + $_.Exception.Message)
        # запасной путь - diskpart
        $script = @"
select disk 0
select partition 3
assign letter=W
"@
        $tmp = 'X:\dp.txt'
        Set-Content -Path $tmp -Value $script -Encoding ASCII
        (diskpart /s $tmp | Out-String)
    }
}
'--- проверка W: ---'
if (Test-Path 'W:\') {
    ('Windows      : ' + (Test-Path 'W:\Windows'))
    ('config\SYSTEM: ' + (Test-Path 'W:\Windows\System32\config\SYSTEM'))
    ('Users        : ' + ((Get-ChildItem 'W:\Users' -Directory -ErrorAction SilentlyContinue).Name -join ', '))
    $v = Get-Volume -DriveLetter W -ErrorAction SilentlyContinue
    if ($v) { ('FS=' + $v.FileSystem + '  free=' + [int]($v.SizeRemaining/1GB) + 'GB / ' + [int]($v.Size/1GB) + 'GB') }
} else { 'W: не смонтировался (возможен BitLocker)' }
