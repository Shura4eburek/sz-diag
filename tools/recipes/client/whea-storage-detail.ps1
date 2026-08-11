$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Полные тела событий WHEA и дисковых ошибок — с привязкой к КОНКРЕТНОМУ устройству.
#
# Грабля (СЗ 161346): штатная секция `diag whea` упала целиком ("The filename or extension
# is too long"), а секция storage печатает `disk 7 bad block on \Device\Harddisk1\DR1` —
# и всё. Какой это физически диск, когда в машине два NVMe плюс воткнутые флешки, из
# такой строки не понять: нумерация Harddisk N плавает от того, что было подключено.
# Здесь Harddisk N резолвится в модель+серийник, а WHEA печатается с источником ошибки
# (Processor Core / PCI Express / Memory) — без этого 0x124 нечем дискриминировать.

$Days = 30
$since = (Get-Date).AddDays(-$Days)

'=== Karta: Harddisk N -> model / serial ==='
# Ntfs/disk/stornvme пишут \Device\HarddiskN\DRN, а Get-PhysicalDisk знает DeviceId.
Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object {
    "   Harddisk$($_.DeviceId) = $($_.FriendlyName) [SN $($_.SerialNumber)] $($_.BusType), $([math]::Round($_.Size/1GB)) GB"
}
''

'=== WHEA-Logger (polnye tela sobytiy) ==='
$w = Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger'; StartTime=$since } -ErrorAction SilentlyContinue
if (-not $w) { '   net' }
else {
    foreach ($e in $w) {
        "--- {0:dd.MM.yyyy HH:mm:ss}  Id={1}  Level={2}" -f $e.TimeCreated, $e.Id, $e.LevelDisplayName
        # Message уже содержит расшифровку источника; XML — на случай если Message пуст.
        if ($e.Message) { ($e.Message -split "`n" | Select-Object -First 12) -join "`n" }
        $x = [xml]$e.ToXml()
        foreach ($n in $x.Event.EventData.Data) {
            if ($n.Name -match 'ErrorSource|ErrorType|Severity|ApicId|MciStat|Bus|Device|Function|Segment|PrimaryDeviceName') {
                "      {0} = {1}" -f $n.Name, $n.'#text'
            }
        }
        ''
    }
}

'=== Diskovye oshibki (disk/stornvme/Ntfs/volmgr) - polnyy tekst ==='
Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='disk','stornvme','Ntfs','volmgr','Disk'; StartTime=$since } -ErrorAction SilentlyContinue |
    Where-Object { $_.LevelDisplayName -in 'Error','Warning','Critical' } |
    Select-Object -First 40 | ForEach-Object {
        $msg = ($_.Message -replace "`r?`n", ' ').Trim()
        "   {0:dd.MM HH:mm:ss} {1,-9} Id={2,-4} {3}" -f $_.TimeCreated, $_.ProviderName, $_.Id, $msg
    }
''

'=== Kernel-Processor-Power / trottling i pitanie ==='
Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Processor-Power'; StartTime=$since } -ErrorAction SilentlyContinue |
    Select-Object -First 15 | ForEach-Object {
        "   {0:dd.MM HH:mm:ss} Id={1} {2}" -f $_.TimeCreated, $_.Id, (($_.Message -replace "`r?`n", ' ').Trim())
    }
''

'=== nvlddmkm / display (Id 153 i sosedi) ==='
# Грабля (СЗ 160697): если провайдера в системе НЕТ вообще (nvlddmkm не пишет событий),
# Get-WinEvent валится с ошибкой и exit code 1 — секция выглядит просто пустой, и её
# легко прочитать как «проверено, чисто». Поэтому провайдеры фильтруются по факту
# наличия, а пустой результат печатается явной строкой.
$want = 'nvlddmkm', 'Display', 'Microsoft-Windows-DxgKrnl'
$have = @(Get-WinEvent -ListProvider * -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in $want } | Select-Object -ExpandProperty Name)
if (-not $have) {
    ('   (provayderov net v sisteme: ' + ($want -join ', ') + ')')
} else {
    $ev = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName=$have; StartTime=$since } -ErrorAction SilentlyContinue)
    if ($ev) {
        $ev | Select-Object -First 15 | ForEach-Object {
            "   {0:dd.MM HH:mm:ss} {1} Id={2} {3}" -f $_.TimeCreated, $_.ProviderName, $_.Id, (($_.Message -replace "`r?`n", ' ').Trim())
        }
    } else {
        ('   (provaydery est: ' + ($have -join ', ') + '; sobytiy za ' + $Days + ' dney net)')
    }
}
