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

# Таблиця кодів жила окремою копією тут і в C# (SzDiag.Contracts.BugcheckCodes) - на 161211
# два наймасовіші коди (62% подій) були розшифровані в рецепті, але не в RunDiag (бэклог п.197).
# Джерело одне - BugcheckCodes.Names; блок нижче регенерується
# BugcheckCodes.ToPowerShellRecipeTable() і звіряється тестом BugcheckCodesRecipeSyncTests.
# BEGIN bugcheck-codes (generated - see BugcheckCodes.ToPowerShellRecipeTable, do not edit by hand)
$codes = @{
    'a' = '0xA IRQL_NOT_LESS_OR_EQUAL'
    '18' = '0x18 REFERENCE_BY_POINTER'
    '19' = '0x19 BAD_POOL_HEADER'
    '1a' = '0x1A MEMORY_MANAGEMENT'
    '1e' = '0x1E KMODE_EXCEPTION_NOT_HANDLED'
    '24' = '0x24 NTFS_FILE_SYSTEM'
    '3b' = '0x3B SYSTEM_SERVICE_EXCEPTION'
    '44' = '0x44 MULTIPLE_IRP_COMPLETE_REQUESTS'
    '4e' = '0x4E PFN_LIST_CORRUPT'
    '50' = '0x50 PAGE_FAULT_IN_NONPAGED_AREA'
    '51' = '0x51 REGISTRY_ERROR'
    '5c' = '0x5C HAL_INITIALIZATION_FAILED'
    '7a' = '0x7A KERNEL_DATA_INPAGE_ERROR'
    '7e' = '0x7E SYSTEM_THREAD_EXCEPTION_NOT_HANDLED'
    '7f' = '0x7F UNEXPECTED_KERNEL_MODE_TRAP'
    '9f' = '0x9F DRIVER_POWER_STATE_FAILURE'
    'a0' = '0xA0 INTERNAL_POWER_ERROR'
    'be' = '0xBE ATTEMPTED_WRITE_TO_READONLY_MEMORY'
    'c2' = '0xC2 BAD_POOL_CALLER'
    'c4' = '0xC4 DRIVER_VERIFIER_DETECTED_VIOLATION'
    'c5' = '0xC5 DRIVER_CORRUPTED_EXPOOL'
    'ca' = '0xCA PNP_DETECTED_FATAL_ERROR'
    'd1' = '0xD1 DRIVER_IRQL_NOT_LESS_OR_EQUAL'
    'ef' = '0xEF CRITICAL_PROCESS_DIED'
    'f4' = '0xF4 CRITICAL_OBJECT_TERMINATION'
    'f7' = '0xF7 DRIVER_OVERRAN_STACK_BUFFER'
    'fc' = '0xFC ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY'
    '101' = '0x101 CLOCK_WATCHDOG_TIMEOUT'
    '109' = '0x109 CRITICAL_STRUCTURE_CORRUPTION'
    '113' = '0x113 VIDEO_DXGKRNL_FATAL_ERROR'
    '116' = '0x116 VIDEO_TDR_ERROR'
    '117' = '0x117 VIDEO_TDR_TIMEOUT_DETECTED'
    '119' = '0x119 VIDEO_SCHEDULER_INTERNAL_ERROR'
    '124' = '0x124 WHEA_UNCORRECTABLE_ERROR'
    '133' = '0x133 DPC_WATCHDOG_VIOLATION'
    '139' = '0x139 KERNEL_SECURITY_CHECK_FAILURE'
    '13a' = '0x13A KERNEL_MODE_HEAP_CORRUPTION'
    '141' = '0x141 VIDEO_ENGINE_TIMEOUT_DETECTED'
    '144' = '0x144 BUGCODE_USB3_DRIVER'
    '154' = '0x154 UNEXPECTED_STORE_EXCEPTION'
    '18b' = '0x18B SECURE_KERNEL_ERROR'
    '193' = '0x193 VIDEO_DXGKRNL_LIVEDUMP'
    '1a8' = '0x1A8 WATCHDOG_LIVEDUMP'
    '1b8' = '0x1B8 WATCHDOG_LIVEDUMP_DXGK'
    '1c8' = '0x1C8 WATCHDOG_LIVEDUMP'
}
# END bugcheck-codes

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
