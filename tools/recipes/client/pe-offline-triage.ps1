$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Тріаж диска клієнта з WinPE, коли Windows не завантажується (СЗ 161972).
#
# Грабля: агент піднятий з PE, штатний `szcli diag run` бачить лише PE (X:\), а вся правда —
# на офлайн-томі клієнта. Довелось руками збирати розмітку, стан завантажувача, дампи,
# hive-и й ознаки гібернації. Цей рецепт робить це одним заходом.
#
# Параметр $Sys — літера тому з Windows клієнта (за замовчуванням шукає сам).
#
# #137 / б.192 (161556): зведений список обмежень WinPE, спільний для ВСІХ pe-*.ps1 —
# щоб кожен рецепт не наступав на ті самі граблі по колу.
#   • `Get-PnpDevice` — немає модуля PnpDevice в PE (`CommandNotFoundException`).
#     Заміна: `Get-CimInstance Win32_PnPEntity`.
#   • `Get-StorageReliabilityCounter` в PE віддає лише `Temperature`/`Wear` —
#     `PowerOnHours`/`StartStopCycles` порожні (а саме наробіток відповідає на «SSD
#     дійсно замінили чи клонували»). Заміна для NVMe: `nvme-smart.ps1` — читає
#     Health Information Log (page 02h) напряму через IOCTL_STORAGE_QUERY_PROPERTY,
#     від командлетів Storage-модуля не залежить і в PE лишається живим.
#   • `Get-Volume`/буква диска — том з Windows часто БЕЗ літери (нижче монтуємо самі в W:).
#   • `param(...)` бути НЕ може, якщо перед ним стоїть будь-який вираз (див. нижче) —
#     має бути ПЕРШИМ рядком файлу, інакше "The term 'param' is not recognized".
#   • Час подій (`Get-WinEvent -Path`) конвертується в локаль машини, що ЧИТАЄ лог
#     (PE), а не клієнта — без перерахунку таймлайн бреше на розбіжність таймзон
#     (161498, б.227).

# УВАГА: `param()` тут бути НЕ може — перший рядок скрипта вже займає $OutputEncoding,
# а PowerShell вимагає param першим виразом (на 161669 це впало з
# "The term 'param' is not recognized"). Том задається змінною нижче.
$Sys = ''

if (-not $Sys) {
    foreach ($l in [char[]]'CDEFGHIJKLMN') {
        if (Test-Path "${l}:\Windows\System32\config\SYSTEM") { $Sys = "${l}:"; break }
    }
}
# У PE том клієнта часто взагалі БЕЗ літери (161669: диск 0 розділ 3, 930 ГБ, letter порожня) —
# тоді монтуємо його самі у W: і шукаємо ще раз.
if (-not $Sys) {
    foreach ($p in (Get-Partition -ErrorAction SilentlyContinue | Where-Object { -not $_.DriveLetter -and $_.Size -gt 40GB })) {
        try { Set-Partition -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -NewDriveLetter W -ErrorAction Stop } catch { continue }
        Start-Sleep -Seconds 2
        if (Test-Path 'W:\Windows\System32\config\SYSTEM') { $Sys = 'W:'; break }
    }
}
if (-not $Sys) { 'Том з Windows не знайдено'; exit 1 }
"=== том клієнта: $Sys ==="

'--- диски ---'
Get-CimInstance Win32_DiskDrive | Select-Object Index,Model,SerialNumber,
    @{n='GB';e={[math]::Round($_.Size/1GB,1)}},InterfaceType,Partitions |
    Format-Table -Auto | Out-String -Width 200
'--- розділи диска 0 ---'
Get-Partition -DiskNumber 0 -ErrorAction SilentlyContinue |
    Select-Object PartitionNumber,DriveLetter,@{n='GB';e={[math]::Round($_.Size/1GB,2)}},Type,IsActive,IsHidden |
    Format-Table -Auto | Out-String -Width 200
Get-Disk 0 | Select-Object Number,FriendlyName,PartitionStyle,HealthStatus,OperationalStatus,IsOffline,IsReadOnly |
    Format-List | Out-String

'--- дампи BSOD ---'
$md = "$Sys\Windows\Minidump"
if (Test-Path $md) {
    Get-ChildItem $md -Filter *.dmp | Sort-Object LastWriteTime |
        Select-Object LastWriteTime,Name,@{n='KB';e={[math]::Round($_.Length/1KB)}} |
        Format-Table -Auto | Out-String -Width 200
} else { 'Minidump: немає' }
if (Test-Path "$Sys\Windows\MEMORY.DMP") {
    Get-Item "$Sys\Windows\MEMORY.DMP" | Select-Object LastWriteTime,@{n='MB';e={[math]::Round($_.Length/1MB)}} | Format-List | Out-String
} else { 'MEMORY.DMP: немає' }
if (Test-Path "$Sys\Windows\LiveKernelReports") {
    Get-ChildItem "$Sys\Windows\LiveKernelReports" -Recurse -File |
        Select-Object LastWriteTime,FullName,@{n='MB';e={[math]::Round($_.Length/1MB,1)}} |
        Format-Table -Auto | Out-String -Width 220
}

# #136 / б.191 (161556): System.evtx давав 35 x Kernel-Power 41 і НУЛЬ BugCheck 1001/WHEA -
# "причини немає" за журналом. Справжня причина лежала у WER: 9 x LiveKernelEvent 0x141
# (VIDEO_ENGINE_TIMEOUT_DETECTED) з парними WATCHDOG-дампами, кожен за хвилину до вимкнона.
# Повний розбір (усі типи, зведення по кодах) - окремим рецептом pe-wer-livekernel.ps1;
# тут - компактна секція одним заходом разом з рештою тріажу. Таблиця кодів
# регенерується з BugcheckCodes.ToPowerShellRecipeTable(), звірка - BugcheckCodesRecipeSyncTests.
'--- WER: LiveKernelEvent / BSOD ---'
# BEGIN bugcheck-codes (generated - see BugcheckCodes.ToPowerShellRecipeTable, do not edit by hand)
$werCodes = @{
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
$werDirs = "$Sys\ProgramData\Microsoft\Windows\WER\ReportArchive", "$Sys\ProgramData\Microsoft\Windows\WER\ReportQueue"
$werAll = Get-ChildItem $werDirs -Directory -ErrorAction SilentlyContinue
"звітів усього: $($werAll.Count)"
$werRows = foreach ($d in ($werAll | Where-Object { $_.Name -match '^(Kernel_|Critical_)' })) {
    $wer = Join-Path $d.FullName 'Report.wer'
    if (-not (Test-Path $wer)) { continue }
    $h = @{}
    foreach ($line in (Get-Content $wer -ErrorAction SilentlyContinue)) {
        if ($line -match '^EventType=(.+)$')           { $h.Type = $Matches[1] }
        if ($line -match '^EventTime=(\d+)$')          { $h.Time = [datetime]::FromFileTimeUtc([int64]$Matches[1]) }
        if ($line -match '^Sig\[(\d+)\]\.Value=(.+)$') { $h["v$($Matches[1])"] = $Matches[2] }
    }
    if ($h.Type -notmatch 'LiveKernelEvent|BlueScreen') { continue }
    [pscustomobject]@{ Time = $h.Time; Type = $h.Type; Code = $h.v0 }
}
if (-not $werRows) { 'LiveKernelEvent/BSOD у WER немає' }
# I-19 (ревью волны 2): банер друкувався навіть коли $werRows порожній - нижче тоді нічого
# немає, і рядок про UTC висить сам по собі без сенсу.
if ($werRows) { '!!! часи нижче - UTC (не локальний час PE, не локальний час клієнта) !!!' }
foreach ($r in ($werRows | Sort-Object Time)) {
    # I-19: $werCodes[$r.Code] падав, якщо у звіту немає Sig[0].Value ($r.Code = $null) -
    # індексація за $null валилась "Index operation failed; the array index evaluated to null".
    $what = if ($r.Code -and $werCodes[$r.Code]) { $werCodes[$r.Code] } else { "код $($r.Code)" }
    '{0:yyyy-MM-dd HH:mm:ss} {1,-16} {2}' -f $r.Time, $r.Type, $what
}
$werLog = "$Sys\Windows\System32\winevt\Logs\System.evtx"
if ((Test-Path $werLog) -and $werRows) {
    # I-19: `Get-WinEvent -Path` без фільтра матеріалізує весь System.evtx і фільтрує вже в
    # PowerShell - у PE це хвилини. FilterHashtable фільтрує на боці провайдера. Крім того,
    # коли Kernel-Power 41 у логу немає, конвеєр давав один $null (у PS 5.1 "$null |
    # ForEach-Object" ітерується ОДИН раз), і виклик .ToUniversalTime() на ньому падав
    # "You cannot call a method on a null-valued expression" - обгортаємо @() і фільтруємо
    # $null явно.
    $k41 = @(Get-WinEvent -FilterHashtable @{ Path = $werLog; Id = 41; ProviderName = 'Microsoft-Windows-Kernel-Power' } -ErrorAction SilentlyContinue |
        ForEach-Object { $_.TimeCreated.ToUniversalTime() })
    foreach ($r in ($werRows | Where-Object { $_.Type -eq 'LiveKernelEvent' } | Sort-Object Time)) {
        $near = $k41 | Where-Object { $_ -ge $r.Time -and ($_ - $r.Time).TotalMinutes -le 120 } |
            Sort-Object | Select-Object -First 1
        if ($near) {
            '{0:yyyy-MM-dd HH:mm:ss} код {1} -> вимкнон через {2} хв' -f $r.Time, $r.Code,
                [math]::Round(($near - $r.Time).TotalMinutes)
        }
    }
}
'докладніше: pe-wer-livekernel.ps1 (усі типи, зведення по кодах, сміття Edge crashpad_log відсічене)'

'--- ознаки гібернації / fast startup ---'
# hiberfil свіжіший за останній запис журналу = система «вимкнена» у сплячці,
# і мертвий старт може бути саме зависанням resume, а не дефектом заліза.
foreach ($f in 'hiberfil.sys','pagefile.sys','swapfile.sys') {
    $p = "$Sys\$f"
    if (Test-Path $p) { $i = Get-Item $p -Force; "$f : $([math]::Round($i.Length/1GB,2)) GB  mtime=$($i.LastWriteTime)" }
    else { "$f : немає" }
}
if (Test-Path "$Sys\Windows\bootstat.dat") { 'bootstat.dat mtime=' + (Get-Item "$Sys\Windows\bootstat.dat").LastWriteTime }
& fsutil dirty query $Sys 2>&1 | Out-String

'--- завантажувач (ESP + BCD) ---'
mountvol S: /s 2>&1 | Out-Null
if (Test-Path 'S:\EFI\Microsoft\Boot\BCD') {
    (& bcdedit /store S:\EFI\Microsoft\Boot\BCD /enum '{default}' 2>&1 | Out-String -Width 200)
    'bootmgfw.efi: ' + (Test-Path 'S:\EFI\Microsoft\Boot\bootmgfw.efi')
} else { 'BCD на ESP не знайдено' }
"winload.efi: $(Test-Path "$Sys\Windows\System32\winload.efi")"

'--- hive-и реєстру ---'
Get-ChildItem "$Sys\Windows\System32\config" -File |
    Where-Object { $_.Name -match '^(SYSTEM|SOFTWARE|SAM|SECURITY|DEFAULT)$' } |
    Select-Object Name,@{n='MB';e={[math]::Round($_.Length/1MB,1)}},LastWriteTime |
    Format-Table -Auto | Out-String -Width 200
$srt = "$Sys\Windows\System32\LogFiles\Srt\SrtTrail.txt"
if (Test-Path $srt) { '--- SrtTrail ---'; (Get-Item $srt).LastWriteTime; Get-Content $srt -Tail 40 } else { 'SrtTrail.txt: немає' }
