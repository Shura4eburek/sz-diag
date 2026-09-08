# Разбор `System.evtx`, ВЫКАЧАННОГО с клиента (`szcli pull`), на сервисном боксе:
# таймлайн отказов + сколько прожил каждый заход + расшифровка WHEA.
#
# Чем отличается от клиентских `pe-offline-events.ps1` / `pe-session-life.ps1`:
# те гоняются агентом ИЗ PE по офлайн-тому, этот — на боксе по уже забранному файлу.
# Нужен, когда машина вырубается прямо в PE (161642: агент отваливался посреди `exec`,
# один заход в PE не прожил и пары минут) — журнал успеваем вытащить за один `pull`,
# а дальше разбираем сколько угодно, уже без клиента.
#
# Грабли:
#  - фильтровать evtx ТОЛЬКО по Id нельзя: 17/18/20/153 заняты одновременно у `Kernel-Boot`,
#    `WHEA-Logger` и `disk` — первый прогон на 161642 дал 482 события мусора вместо 156
#    по делу. Фильтр — по паре «провайдер + id».
#  - `Kernel-Power 41` и WHEA пишутся ПОСЛЕ старта (Windows вычитывает банки MCA, пережившие
#    ребут) и описывают ПРЕДЫДУЩИЙ заход — иначе выйдет «упало сразу после загрузки» (б.227).
#  - `TimeCreated` конвертируется в таймзону машины, которая ЧИТАЕТ файл. Бокс и клиент в
#    одной зоне — совпадает; если файл приехал с машины из другой зоны, печатать UTC.
#
# Использование: .\evtx-offline-report.ps1 -Path <System.evtx> [-Days 60]

param(
    [Parameter(Mandatory)][string]$Path,
    [int]$Days = 60
)

$ErrorActionPreference = 'Stop'
$since = (Get-Date).AddDays(-$Days)

# провайдер → интересные id (пустой массив = все события провайдера)
$want = @{
    'Microsoft-Windows-Kernel-Power'             = @(41, 109, 137, 142)  # 41 hard-off/BSOD, 109 ядро гасит
    'Microsoft-Windows-WHEA-Logger'              = @()
    'EventLog'                                   = @(6008, 6005, 6006)
    'User32'                                     = @(1074)               # кто инициировал ребут
    'Microsoft-Windows-WER-SystemErrorReporting' = @(1001)               # BSOD: код + путь к дампу
    'BugCheck'                                   = @(1001)
    'volmgr'                                     = @(161, 162)           # дамп не записан
    'disk'                                       = @(7, 11, 51, 52, 153)
    'storahci'                                   = @(129)
    'stornvme'                                   = @(129)
    'nvlddmkm'                                   = @()
    'amdkmdag'                                   = @()
    'Display'                                    = @(4101)
    'Microsoft-Windows-Kernel-Processor-Power'   = @(37, 38)
}

Write-Host "Читаю $Path (события за $Days дн.)..." -ForegroundColor Cyan
$all = Get-WinEvent -Path $Path -ErrorAction SilentlyContinue | Where-Object { $_.TimeCreated -ge $since }

$events = $all | Where-Object {
    $ids = $want[$_.ProviderName]
    ($null -ne $ids) -and ($ids.Count -eq 0 -or $ids -contains $_.Id)
} | Sort-Object TimeCreated

Write-Host ("Событий по делу: {0}" -f @($events).Count) -ForegroundColor Cyan

function Get-Data($e) {
    $h = @{}
    try { foreach ($d in ([xml]$e.ToXml()).Event.EventData.Data) { $h[$d.Name] = $d.'#text' } } catch { }
    return $h
}

# ── 1. Таймлайн ────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Таймлайн ===" -ForegroundColor Cyan
$events | ForEach-Object {
    $note = if ($_.Id -eq 41 -and $_.ProviderName -eq 'Microsoft-Windows-Kernel-Power') {
        $p = Get-Data $_
        "BugcheckCode={0} PowerButton={1} SleepInProgress={2}" -f $p['BugcheckCode'], $p['PowerButtonTimestamp'], $p['SleepInProgress']
    }
    else {
        ($_.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 2) -join ' | '
    }
    [pscustomobject]@{
        Время    = $_.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')
        Id       = $_.Id
        Источник = $_.ProviderName -replace '^Microsoft-Windows-', ''
        Что      = ($note -replace '\s+', ' ').Trim()
    }
} | Format-Table -AutoSize -Wrap

# ── 2. Заходы: сколько прожил и чем кончился ──────────────────────────────────
Write-Host ""
Write-Host "=== Заходы (старт журнала → конец) ===" -ForegroundColor Cyan
$runs = New-Object System.Collections.ArrayList
$start = $null; $whea = 0
foreach ($e in ($all | Sort-Object TimeCreated)) {
    if ($e.ProviderName -like '*WHEA*') { $whea++; continue }
    if ($e.ProviderName -eq 'EventLog' -and $e.Id -eq 6005) { $start = $e.TimeCreated; $whea = 0; continue }
    $isEnd = ($e.ProviderName -eq 'EventLog' -and $e.Id -eq 6006) -or
             ($e.ProviderName -eq 'Microsoft-Windows-Kernel-Power' -and $e.Id -eq 41)
    if (-not $isEnd) { continue }

    $kind = if ($e.Id -eq 6006) { 'штатно' }
    else {
        $bc = (Get-Data $e)['BugcheckCode']
        if ($bc -and $bc -ne '0') { "BSOD 0x{0:X}" -f [int]$bc } else { 'ВЫРУБОН (без дампа)' }
    }
    if ($start) {
        [void]$runs.Add([pscustomobject]@{
                Старт    = $start.ToString('yyyy-MM-dd HH:mm:ss')
                Держался = '{0,8:N1} мин' -f ($e.TimeCreated - $start).TotalMinutes
                Конец    = $e.TimeCreated.ToString('HH:mm:ss')
                Итог     = $kind
                WHEA     = $whea
            })
    }
    $start = $null; $whea = 0
}
$runs | Format-Table -AutoSize

$bad = @($runs | Where-Object Итог -ne 'штатно')
Write-Host ("Заходов: {0}, аварийных: {1}" -f $runs.Count, $bad.Count) -ForegroundColor Yellow
if ($bad.Count) {
    $mins = $bad | ForEach-Object { [double](($_.Держался -replace '[^\d,.]', '') -replace ',', '.') } | Sort-Object
    Write-Host ("Аварийный заход: медиана {0:N1} мин, мин {1:N1}, макс {2:N1}" -f `
            $mins[[int]($mins.Count / 2)], $mins[0], $mins[-1]) -ForegroundColor Yellow
}

# ── 3. WHEA в деталях ─────────────────────────────────────────────────────────
# Строка «A fatal hardware error has occurred» сама по себе не значит ничего: вердикт дают
# источник (Machine Check Exception vs PCIe/NMI), банк MCA и флаги MciStat.
Write-Host ""
Write-Host "=== WHEA ===" -ForegroundColor Cyan
foreach ($e in ($all | Where-Object { $_.ProviderName -like '*WHEA*' } | Sort-Object TimeCreated)) {
    $d = Get-Data $e
    Write-Host ("--- {0}  Id={1}  {2}" -f $e.TimeCreated, $e.Id, $e.LevelDisplayName) -ForegroundColor Yellow
    foreach ($k in 'ErrorSource', 'ErrorType', 'ApicId', 'MCABank', 'MciStat', 'MciAddr', 'MciMisc') {
        if ($d.ContainsKey($k)) { Write-Host ("    {0,-9} {1}" -f $k, $d[$k]) }
    }
    if ($d['MciStat']) {
        # Флаги MCi_STATUS: VAL(63) OVER(62) UC(61) EN(60) MISCV(59) ADDRV(58) PCC(57).
        # UC=1 + PCC=1 = некорректируемая, контекст ядра испорчен → машина обязана упасть.
        # Парсим строку как HexNumber: обычный [uint64]"0x..." в PowerShell не приводится.
        $v = [uint64]::Parse(($d['MciStat'] -replace '^0[xX]', ''), 'HexNumber')
        $bits = [ordered]@{ VAL = 63; OVER = 62; UC = 61; EN = 60; MISCV = 59; ADDRV = 58; PCC = 57 }
        $f = foreach ($n in $bits.Keys) {
            $mask = [uint64][math]::Pow(2, $bits[$n])
            if (($v -band $mask) -ne 0) { $n }
        }
        Write-Host ("    флаги     {0}; MCA error code 0x{1:X4}" -f ($f -join ' '), ($v -band 0xFFFF))
    }
    Write-Host ("    msg       {0}" -f (($e.Message -split "`r?`n" | Where-Object { $_.Trim() }) -join ' | '))
}
