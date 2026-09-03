# Розбір System.evtx **офлайн-тому клієнта** з WinPE (СЗ 161972).
#
# Грабля: коли Windows не вантажиться, штатні секції `diag` бачать лише PE. А журнал клієнта
# лежить файлом і чудово читається `Get-WinEvent -Path`. Саме він показав, що падіння сталося
# ПІСЛЯ чистого завершення роботи й до старту служби журналу (у журналі — тиша), тобто BSOD
# на ранньому етапі, без мінідампа.
#
# Друкує: діапазон журналу, BugCheck/41/6008, помилки NTFS і диска, топ помилок, WHEA,
# і хвіст журналу — що система робила перед смертю.
#
# ВНИМАНИЕ: param обязан быть ПЕРВОЙ инструкцией файла (до правки тут стоял $OutputEncoding
# раньше param — скрипт не парсился вообще, ни через pwsh -File, ни через `szcli exec -f`).

param([string]$Sys = '', [int]$Tail = 60, [int]$Cap = 50)
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()

if (-not $Sys) {
    foreach ($l in [char[]]'CDEFGHIJ') {
        if (Test-Path "${l}:\Windows\System32\config\SYSTEM") { $Sys = "${l}:"; break }
    }
}
$log = "$Sys\Windows\System32\winevt\Logs\System.evtx"
if (-not (Test-Path $log)) { "System.evtx не знайдено ($log)"; exit 1 }

$ev = Get-WinEvent -Path $log -ErrorAction SilentlyContinue
"журнал: $log"
"записів: $($ev.Count)"
"діапазон: $(($ev | Select-Object -Last 1).TimeCreated) .. $(($ev | Select-Object -First 1).TimeCreated)"

function Show($e) {
    '{0:yyyy-MM-dd HH:mm:ss} [{1}/{2}/{3}] {4}' -f $e.TimeCreated, $e.ProviderName, $e.Id,
        $e.LevelDisplayName, (($e.Message -replace '\s+', ' ') -replace '^(.{240}).*', '$1')
}

# #135 / б.190 (161556, 38 938 записів / 7 місяців): `Ntfs/98` ("Volume C: is healthy") —
# Information, що прилітає двічі на кожне завантаження, сотні штук на журнал. Разом з
# необмеженим виводом секцій це впирається в ліміт `exec` (200k символів) і рецепт **обривається
# на середині** — WHEA, топ помилок і хвіст журналу до консолі взагалі не доїжджають, хоча
# рецепт формально відпрацював без помилки. Тому: (1) шум рівня Information не тягнемо в секції
# нижче, (2) кожна секція друкує не більше $Cap подій і чесно каже «показано N із M», щоб
# мовчазне обрізання не виглядало як «подій більше немає».
function ShowSection([string]$Title, $Events, [int]$CapN = $Cap) {
    $Title
    $all = @($Events | Sort-Object TimeCreated)
    $total = $all.Count
    if ($total -eq 0) { 'порожньо'; return }
    $shown = if ($total -gt $CapN) { $all | Select-Object -Last $CapN } else { $all }
    if ($total -gt $CapN) { "показано $($shown.Count) з $total (останні)" }
    $shown | ForEach-Object { Show $_ }
}

$noNoise = $ev | Where-Object { $_.LevelDisplayName -notin 'Information', 'Сведения', 'Інформація' }

ShowSection '=== BugCheck 1001 / Kernel-Power 41 / EventLog 6008 ===' (
    $noNoise | Where-Object { ($_.Id -eq 1001 -and $_.ProviderName -match 'BugCheck') -or $_.Id -eq 6008 -or $_.Id -eq 41 }
)

ShowSection '=== NTFS / диск / завантаження ===' (
    $noNoise | Where-Object {
        ($_.ProviderName -match 'Ntfs' -and $_.Id -in 55, 137, 140) -or
        ($_.ProviderName -match 'Kernel-Boot' -and $_.Id -eq 29) -or
        ($_.Id -in 7, 11, 153 -and $_.ProviderName -match 'disk|Disk|stornvme|storahci')
    }
)

'=== WHEA ==='
$w = $ev | Where-Object { $_.ProviderName -match 'WHEA' }
if ($w) { $w | Group-Object Id, LevelDisplayName | Format-Table -Auto | Out-String -Width 200 } else { 'порожньо' }

'=== топ помилок ==='
$ev | Where-Object { $_.LevelDisplayName -in 'Ошибка', 'Error', 'Помилка', 'Критический', 'Critical', 'Критична' } |
    Group-Object ProviderName, Id | Sort-Object Count -Descending | Select-Object -First 25 Count, Name |
    Format-Table -Auto | Out-String -Width 200

ShowSection "=== останні $Tail подій (що було перед смертю) ===" $ev $Tail
