$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Розбір WER (`ReportArchive`/`ReportQueue`) офлайн-тому клієнта — LiveKernelEvent і BSOD (СЗ 161556).
#
# Грабля: на 161556 у System.evtx було 35 × Kernel-Power 41 і **нуль** BugCheck 1001, нуль WHEA,
# нуль помилок диска — за журналом «причини немає». Причина лежала у WER: 9 × LiveKernelEvent
# **0x141** (VIDEO_ENGINE_TIMEOUT_DETECTED — відеодвигун завис) з парними WATCHDOG-дампами,
# кожен за хвилину до вимкнона. Штатний `pe-offline-triage.ps1` WER не дивиться, а `Display/4101`
# («драйвер відновлено») при TDR, що НЕ відновився, у журнал не пишеться взагалі.
#
# Друкує: зведення по типах, таймлайн LiveKernelEvent/BSOD з розшифровкою кодів і назвами дампів.
# Сміття (crashpad_log від Edge — на 161556 це 351 звіт із 538) відсікається.
#
# param не використовуємо: `szcli exec -f` його не переварює (бэклог п.189).

$Sys = ''
foreach ($l in [char[]]'CDEFGHIJ') {
    if (Test-Path "${l}:\Windows\System32\config\SYSTEM") { $Sys = "${l}:"; break }
}
if (-not $Sys) { 'Том з Windows не знайдено'; exit 1 }
"=== том клієнта: $Sys ==="

$codes = @{
    '141' = '0x141 VIDEO_ENGINE_TIMEOUT_DETECTED — TDR, відеодвигун не відповів'
    '117' = '0x117 VIDEO_TDR_TIMEOUT_DETECTED'
    '144' = '0x144 USB (BadDeviceReset/xHCI)'
    '1a8' = '0x1a8 watchdog live dump'
    '1b8' = '0x1b8 watchdog live dump (dxgk)'
    '1c8' = '0x1c8 watchdog live dump'
}

$dirs = "$Sys\ProgramData\Microsoft\Windows\WER\ReportArchive",
        "$Sys\ProgramData\Microsoft\Windows\WER\ReportQueue"
$all = Get-ChildItem $dirs -Directory -ErrorAction SilentlyContinue
"звітів усього: $($all.Count)"

'--- зведення по типах ---'
$all | ForEach-Object { ($_.Name -split '_')[0..1] -join '_' } | Group-Object |
    Sort-Object Count -Descending | Select-Object -First 25 Count, Name |
    Format-Table -Auto | Out-String -Width 100

'--- LiveKernelEvent / BSOD: таймлайн ---'
$rows = foreach ($d in ($all | Where-Object { $_.Name -match '^(Kernel_|Critical_)' })) {
    $wer = Join-Path $d.FullName 'Report.wer'
    if (-not (Test-Path $wer)) { continue }
    $h = @{}
    foreach ($line in (Get-Content $wer -ErrorAction SilentlyContinue)) {
        if ($line -match '^EventType=(.+)$')           { $h.Type = $Matches[1] }
        if ($line -match '^EventTime=(\d+)$')          { $h.Time = [datetime]::FromFileTime([int64]$Matches[1]) }
        if ($line -match '^Sig\[(\d+)\]\.Value=(.+)$') { $h["v$($Matches[1])"] = $Matches[2] }
    }
    if ($h.Type -notmatch 'LiveKernelEvent|BlueScreen') { continue }
    $dumps = (Get-ChildItem $d.FullName -File -Filter *.dmp -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Name }) -join ','
    [pscustomobject]@{
        Time  = $h.Time
        Type  = $h.Type
        Code  = $h.v0
        Sig   = (1..4 | ForEach-Object { $h["v$_"] }) -join ' '
        Dumps = $dumps
    }
}
if (-not $rows) { 'LiveKernelEvent/BSOD у WER немає' }
foreach ($r in ($rows | Sort-Object Time)) {
    $what = if ($codes[$r.Code]) { $codes[$r.Code] } else { "код $($r.Code)" }
    '{0:yyyy-MM-dd HH:mm:ss} {1,-16} {2}' -f $r.Time, $r.Type, $what
    if ($r.Sig.Trim())   { '    параметри: {0}' -f $r.Sig }
    if ($r.Dumps)        { '    дампи: {0}' -f $r.Dumps }
}

'--- зведення по кодах LiveKernelEvent ---'
$rows | Where-Object { $_.Type -eq 'LiveKernelEvent' } | Group-Object Code |
    Sort-Object Count -Descending |
    ForEach-Object {
        $c = if ($codes[$_.Name]) { $codes[$_.Name] } else { "код $($_.Name)" }
        '{0,3} × {1}' -f $_.Count, $c
    }

'--- звірка з вимкнонами (Kernel-Power 41) ---'
$log = "$Sys\Windows\System32\winevt\Logs\System.evtx"
if (Test-Path $log) {
    $k41 = (Get-WinEvent -Path $log -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -eq 41 -and $_.ProviderName -match 'Kernel-Power' }).TimeCreated
    foreach ($r in ($rows | Where-Object { $_.Type -eq 'LiveKernelEvent' } | Sort-Object Time)) {
        $near = $k41 | Where-Object { $_ -ge $r.Time -and ($_ - $r.Time).TotalMinutes -le 120 } |
            Sort-Object | Select-Object -First 1
        if ($near) {
            '{0:yyyy-MM-dd HH:mm:ss} код {1} → вимкнон через {2} хв' -f $r.Time, $r.Code,
                [math]::Round(($near - $r.Time).TotalMinutes)
        }
    }
}
