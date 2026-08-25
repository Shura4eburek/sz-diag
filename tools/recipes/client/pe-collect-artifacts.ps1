$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Зібрати журнали й дампи офлайн-тому клієнта в одну папку — з WinPE (СЗ 161946).
#
# Грабля: коли машина в PE, штатний `szcli diag run` бачить лише PE, а `szcli pull` по цій
# машині мовчки висне до таймауту (бэклог п.215). Тому спершу збираємо все потрібне в одну
# папку на флешці PE, а вже потім віддаємо на хост (див. `pe-file-out-base64.ps1`).
#
# Друкує, що знайшлось і скільки важить. Літеру тому шукає сам.

$Sys = ''
foreach ($l in [char[]]'CDEFGHIJ') {
    if (Test-Path "${l}:\Windows\System32\config\SYSTEM") { $Sys = "${l}:"; break }
}
if (-not $Sys) { 'том з Windows не знайдено'; exit 1 }
"том клієнта: $Sys"

# складаємо на флешку PE, а не на X: (RAM-диск маленький і зникає з ребутом)
$dst = 'D:\szdiag-artifacts'
if (-not (Test-Path 'D:\')) { $dst = 'X:\szdiag-artifacts' }
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
New-Item -ItemType Directory -Path $dst | Out-Null

$logs = "$Sys\Windows\System32\winevt\Logs"
$want = @(
    'System.evtx','Application.evtx',
    'Microsoft-Windows-Kernel-WHEA%4Operational.evtx',
    'Microsoft-Windows-Kernel-Power%4Thermal-Operational.evtx',
    'Microsoft-Windows-Kernel-PnP%4Configuration.evtx',
    'Microsoft-Windows-Kernel-Boot%4Operational.evtx',
    'Microsoft-Windows-Ntfs%4Operational.evtx',
    'Microsoft-Windows-Storage-Storport%4Operational.evtx',
    'Microsoft-Windows-Partition%4Diagnostic.evtx',
    'Microsoft-Windows-Diagnostics-Performance%4Operational.evtx',
    'Microsoft-Windows-StorDiag%4Operational.evtx',
    'HardwareEvents.evtx'
)
foreach ($f in $want) {
    $p = Join-Path $logs $f
    if (Test-Path $p) { Copy-Item $p $dst -Force; "ok   $f" } else { "miss $f" }
}

# дампи: мінідампи, live kernel reports і WER-кабіни (WATCHDOG*.dmp лежить саме там)
$dumps = @()
foreach ($pat in @("$Sys\Windows\Minidump\*.dmp", "$Sys\Windows\LiveKernelReports\*.dmp",
                   "$Sys\Windows\LiveKernelReports\*\*.dmp", "$Sys\Windows\MEMORY.DMP")) {
    Get-ChildItem $pat -EA SilentlyContinue | ForEach-Object { $dumps += $_ }
}
Get-ChildItem "$Sys\ProgramData\Microsoft\Windows\WER" -Recurse -Include '*.dmp' -EA SilentlyContinue |
    ForEach-Object { $dumps += $_ }
foreach ($d in $dumps) {
    "dump {0,8:N0} KB  {1}  {2:yyyy-MM-dd HH:mm}" -f ($d.Length/1KB), $d.FullName, $d.LastWriteTime
    if ($d.Length -lt 60MB) { Copy-Item $d.FullName (Join-Path $dst $d.Name) -Force }
    else { "     (пропущено: >60 МБ, тягнути окремо)" }
}

"--- зібрано в $dst ---"
Get-ChildItem $dst | ForEach-Object { "{0,8:N0} KB  {1}" -f ($_.Length/1KB), $_.Name }
"разом МБ: {0:N1}" -f ((Get-ChildItem $dst | Measure-Object Length -Sum).Sum/1MB)
