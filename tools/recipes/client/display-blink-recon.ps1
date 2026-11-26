# Короткое пропадание картинки (секунды) без следов в System: где искать причину. Только чтение.
# Грабля (СЗ 168018): Display/nvlddmkm в System пусты, новых WATCHDOG-дампов нет, а LiveKernelEvent
# в Application сыплются сотнями — это переотправки WER, а не сбои. Причина мигания может быть не
# в TDR: установка драйвера через Windows Update, сброс PnP, перезапуск адаптера, HDMI hot-plug.
# Поэтому смотрим каналы, которых нет в System, и новые WER-отчеты отдельно от старых.
#   szcli exec <СЗ> -f tools\recipes\client\display-blink-recon.ps1 --param Days=3 --timeout 180
param([int]$Days = 3)
$since = (Get-Date).AddDays(-$Days)

function Show($ev, $n = 60) {
    $ev | Sort-Object TimeCreated | Select-Object -Last $n | ForEach-Object {
        '   {0:MM-dd HH:mm:ss} {1} id {2} L{3} {4}' -f $_.TimeCreated, ($_.ProviderName -replace 'Microsoft-Windows-', ''), $_.Id, $_.Level, (($_.Message -split "`r?`n")[0])
    }
}

"== boots since $since"
Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 6005; StartTime = $since } -ErrorAction SilentlyContinue |
    Sort-Object TimeCreated | ForEach-Object { '   {0:MM-dd HH:mm:ss}' -f $_.TimeCreated }

'== channels available (dxgkrnl / display / graphics / monitor)'
$chs = @(Get-WinEvent -ListLog * -ErrorAction SilentlyContinue |
    Where-Object { $_.LogName -match 'Dxgk|Display|Graphics|Monitor|Video' -and $_.RecordCount -gt 0 })
$chs | ForEach-Object { '   {0}  records={1}' -f $_.LogName, $_.RecordCount }

foreach ($c in $chs) {
    if ($c.LogName -match 'Dxgk|Display') {
        "== $($c.LogName), last 40"
        Show (Get-WinEvent -FilterHashtable @{ LogName = $c.LogName; StartTime = $since } -ErrorAction SilentlyContinue) 40
    }
}

'== System: display/PnP/driver install/WU providers'
Show (Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -match 'Display|nvlddmkm|amdkmdag|amdwddmg|Dxgk|Kernel-PnP|UserPnp|WindowsUpdateClient|DeviceSetup|Resource-Exhaustion' }) 80

'== Setup log (driver/KB install)'
Show (Get-WinEvent -FilterHashtable @{ LogName = 'Setup'; StartTime = $since } -ErrorAction SilentlyContinue) 25

'== UserPnp / DeviceSetupManager admin'
foreach ($l in 'Microsoft-Windows-DeviceSetupManager/Admin', 'Microsoft-Windows-UserPnp/DeviceInstall') {
    "   -- $l"
    Show (Get-WinEvent -FilterHashtable @{ LogName = $l; StartTime = $since } -ErrorAction SilentlyContinue) 25
}

'== Application: NVIDIA / AMD / dwm errors'
Show (Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -ne 1001 -and ($_.Level -le 3 -or $_.ProviderName -match 'Desktop Window Manager|Dwm') -and ($_.Message -match 'nvidia|nvcontainer|amd|radeon|atiec|dwm|dxgi|d3d' -or $_.ProviderName -match 'Desktop Window Manager|Dwm') }) 30

'== Real new kernel reports: LiveKernelReports files newer than the window'
$new = @(Get-ChildItem C:\Windows\LiveKernelReports -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt $since })
"   count=$($new.Count)"
$new | Sort-Object LastWriteTime | ForEach-Object { '   {0:MM-dd HH:mm:ss} {1}' -f $_.LastWriteTime, $_.Name }

'== WER report folders: distinct dump names vs folder count, window'
$dirs = @(Get-ChildItem C:\ProgramData\Microsoft\Windows\WER\ReportArchive, C:\ProgramData\Microsoft\Windows\WER\ReportQueue -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^Kernel_' -and $_.LastWriteTime -gt $since })
"   folders=$($dirs.Count)"
$dirs | Group-Object { ($_.Name -split '_')[1] } | ForEach-Object { '   code {0}: {1}' -f $_.Name, $_.Count }

'== Adapters (PnP status, problem code)'
Get-CimInstance Win32_VideoController | ForEach-Object {
    '   {0} / drv {1} / status {2} / err {3} / mode {4}x{5}@{6}' -f $_.Name, $_.DriverVersion, $_.Status, $_.ConfigManagerErrorCode, $_.CurrentHorizontalResolution, $_.CurrentVerticalResolution, $_.CurrentRefreshRate
}

'== NVIDIA driver packages in store'
Get-ChildItem C:\Windows\System32\DriverStore\FileRepository -Directory -Filter 'nv*.inf_amd64*' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | ForEach-Object { '   {0:yyyy-MM-dd HH:mm} {1}' -f $_.LastWriteTime, $_.Name }

'== WU: recent history'
try {
    $s = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher()
    $s.QueryHistory(0, [Math]::Min(20, $s.GetTotalHistoryCount())) | ForEach-Object {
        '   {0:yyyy-MM-dd HH:mm} rc={1} {2}' -f $_.Date, $_.ResultCode, $_.Title
    }
} catch { "   WU history failed: $_" }
