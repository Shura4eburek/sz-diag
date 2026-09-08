# Сжатая сводка по выкачанному System.evtx: BSOD/вырубоны/старты + АГРЕГАТ WHEA.
#
# Зачем отдельно от evtx-offline-report.ps1: тот печатает каждое WHEA-событие целиком.
# На 162731 это 400+ одинаковых "corrected PCIe AER" за две секунды — вывод улетел в
# десятки тысяч символов и утопил реальный таймлайн. Здесь WHEA сворачивается в
# «сколько, каких, когда» (грабля: массовые corrected-события идут пачками по секунде,
# считать их надо по группам, а не по штукам).
#
# Использование: .\evtx-summary.ps1 -Path <System.evtx> [-Days 120]

param(
    [Parameter(Mandatory)][string]$Path,
    [int]$Days = 120
)

$ErrorActionPreference = 'Stop'
$since = (Get-Date).AddDays(-$Days)
$ev = Get-WinEvent -Path $Path -Oldest -ErrorAction Stop | Where-Object { $_.TimeCreated -ge $since }

Write-Output "Всего событий за $Days дн.: $($ev.Count)"

Write-Output ''
Write-Output '=== BSOD (BugCheck 1001 / Kernel-Power 41 / чистые выключения 1074) ==='
$ev | Where-Object {
    ($_.ProviderName -eq 'Microsoft-Windows-WER-SystemErrorReporting' -and $_.Id -eq 1001) -or
    ($_.ProviderName -eq 'Microsoft-Windows-Kernel-Power' -and $_.Id -eq 41) -or
    ($_.ProviderName -eq 'EventLog' -and $_.Id -eq 6008)
} | ForEach-Object {
    $m = ($_.Message -replace '\s+', ' ')
    if ($m.Length -gt 200) { $m = $m.Substring(0, 200) }
    '{0:dd.MM.yy HH:mm:ss}  {1,-45} Id={2}  {3}' -f $_.TimeCreated, $_.ProviderName, $_.Id, $m
}

Write-Output ''
Write-Output '=== WHEA: агрегат по типу и дате ==='
$whea = $ev | Where-Object { $_.ProviderName -match 'WHEA' }
$whea | ForEach-Object {
    $m = ($_.Message -replace '\s+', ' ')
    $comp = if ($m -match 'Component:\s*([^|]+)') { $Matches[1].Trim() } else { '?' }
    $kind = if ($m -match 'corrected') { 'corrected' } elseif ($m -match 'fatal|uncorrect') { 'FATAL' } else { 'other' }
    [pscustomobject]@{
        Дата      = $_.TimeCreated.ToString('dd.MM.yy')
        Момент    = $_.TimeCreated.ToString('HH:mm')
        Id        = $_.Id
        Тип       = $kind
        Компонент = $comp
    }
} | Group-Object Дата, Момент, Id, Тип, Компонент | ForEach-Object {
    $p = $_.Group[0]
    '{0} {1}  Id={2,-4} {3,-9} {4,-30} x{5}' -f $p.Дата, $p.Момент, $p.Id, $p.Тип, $p.Компонент, $_.Count
}

Write-Output ''
Write-Output '=== Прочие ошибки уровня Error/Critical (топ провайдеров) ==='
$ev | Where-Object { $_.LevelDisplayName -in @('Ошибка', 'Error', 'Критическая', 'Critical') } |
    Group-Object ProviderName, Id |
    Sort-Object Count -Descending | Select-Object -First 20 |
    ForEach-Object { '{0,5}  {1}' -f $_.Count, $_.Name }
