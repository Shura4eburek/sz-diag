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
