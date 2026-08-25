$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Офлайн-розбір WHEA / термалки / відеогілки з WinPE (СЗ 161946).
#
# Грабля: `pe-offline-events.ps1` дивиться лише System.evtx, а там секція WHEA буває порожня —
# при тому, що окремий журнал `Kernel-WHEA%4Operational` і `Kernel-Power%4Thermal-Operational`
# лежать поруч файлами й чудово читаються `Get-WinEvent -Path`.
#
# Обережно з термалкою: на 161946 у журналі 23 × «ACPI thermal zone has engaged passive cooling»,
# і це виглядає як перегрів — але там `_TMP = 290K` (17 °C) і `Delta P = 0`, тобто зона-пустишка
# плати Gigabyte A520M, яку вона рапортує на кожному старті. Перегрів доводиться приладно
# (lhmmon/OCCT), а не цим рядком.

$Sys = ''
foreach ($l in [char[]]'CDEFGHIJ') {
    if (Test-Path "${l}:\Windows\System32\config\SYSTEM") { $Sys = "${l}:"; break }
}
if (-not $Sys) { 'том з Windows не знайдено'; exit 1 }
$dir = "$Sys\Windows\System32\winevt\Logs"

function Dump($file, $title) {
    $p = Join-Path $dir $file
    "=== $title ==="
    if (-not (Test-Path $p)) { '  немає файлу'; return }
    $ev = Get-WinEvent -Path $p -ErrorAction SilentlyContinue
    if (-not $ev) { '  порожньо'; return }
    "  записів: $($ev.Count), діапазон: $(($ev | Select-Object -Last 1).TimeCreated) .. $(($ev | Select-Object -First 1).TimeCreated)"
    $ev | Group-Object Id | Sort-Object Count -Descending | ForEach-Object { '  {0,4} x id {1}' -f $_.Count, $_.Name }
    $ev | Select-Object -First 25 | ForEach-Object {
        '  {0:yyyy-MM-dd HH:mm:ss} [{1}/{2}] {3}' -f $_.TimeCreated, $_.Id, $_.LevelDisplayName,
            (($_.Message -replace '\s+', ' ') -replace '^(.{300}).*', '$1')
    }
}

Dump 'Microsoft-Windows-Kernel-WHEA%4Operational.evtx'          'Kernel-WHEA'
Dump 'Microsoft-Windows-Kernel-Power%4Thermal-Operational.evtx'  'Kernel-Power Thermal'
Dump 'HardwareEvents.evtx'                                        'HardwareEvents'

'=== System.evtx: дисплей / відео / живлення CPU / диск ==='
$sys = Get-WinEvent -Path "$dir\System.evtx" -ErrorAction SilentlyContinue
$sys | Where-Object {
    $_.ProviderName -match 'Display|nvlddmkm|amdkmdag|Kernel-Processor-Power|WHEA|BugCheck|WER-SystemErrorReporting|volmgr|disk|storahci' -or
    $_.Id -in 4101, 4102, 1001, 219, 37, 86
} | Sort-Object TimeCreated | ForEach-Object {
    '{0:yyyy-MM-dd HH:mm:ss} [{1}/{2}/{3}] {4}' -f $_.TimeCreated, $_.ProviderName, $_.Id, $_.LevelDisplayName,
        (($_.Message -replace '\s+', ' ') -replace '^(.{280}).*', '$1')
} | Select-Object -Last 60
