# Prodolzhenie offline-triazha iz WinPE: zdorovye NVMe, WHEA, chto imenno zavislo v CBS.
# Grabli: tolko ASCII (beklog p.283); ne ispolzovat imya $Sz (zanyato podstanovkoy szcli).
$ErrorActionPreference = 'SilentlyContinue'

$Vol = 'C'
$root = "${Vol}:"
$evtSys = "$root\Windows\System32\winevt\Logs\System.evtx"

Write-Output '=== PE clock (dlya pravki vremeni zhurnalov) ==='
Write-Output ("PE now: " + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + "  UTC: " + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))
Write-Output ("PE TZ bias (min): " + (Get-CimInstance Win32_OperatingSystem).CurrentTimeZone)

Write-Output '=== NVMe health ==='
Get-PhysicalDisk | Select-Object DeviceId, FriendlyName, MediaType, HealthStatus, OperationalStatus,
    @{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}} | Format-Table -AutoSize | Out-String -Width 160
foreach ($pd in Get-PhysicalDisk) {
    $rc = $pd | Get-StorageReliabilityCounter
    if ($rc) {
        Write-Output ("disk {0}: temp={1}C wear={2} readErrCorr={3} readErrUncorr={4} writeErrUncorr={5} powerOnH={6} startStop={7}" -f `
            $pd.DeviceId, $rc.Temperature, $rc.Wear, $rc.ReadErrorsCorrected, $rc.ReadErrorsUncorrected, `
            $rc.WriteErrorsUncorrected, $rc.PowerOnHours, $rc.StartStopCycles)
    }
}

Write-Output '=== WHEA / disk errors / unexpected shutdown (offline System.evtx) ==='
if (Test-Path $evtSys) {
    $whea = Get-WinEvent -FilterHashtable @{ Path = $evtSys; ProviderName = 'Microsoft-Windows-WHEA-Logger' } -MaxEvents 50
    Write-Output ("WHEA sobytiy: " + ($whea | Measure-Object).Count)
    foreach ($e in $whea | Select-Object -First 20) {
        Write-Output ("{0}  Id={1}  {2}" -f $e.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'), $e.Id, (($e.Message -replace '\s+',' ')))
    }
    $dsk = Get-WinEvent -FilterHashtable @{ Path = $evtSys; ProviderName = 'disk','nvme','stornvme','Ntfs','volmgr' } -MaxEvents 60
    Write-Output ("--- disk/nvme/ntfs sobytiy: " + ($dsk | Measure-Object).Count)
    foreach ($e in $dsk | Select-Object -First 25) {
        $m = ($e.Message -replace '\s+',' ')
        if ($m.Length -gt 130) { $m = $m.Substring(0,130) }
        Write-Output ("{0}  {1}/{2}  {3}" -f $e.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'), $e.ProviderName, $e.Id, $m)
    }
}

Write-Output '=== CBS tail (chto stavilos pered srivom) ==='
$cbs = "$root\Windows\Logs\CBS\CBS.log"
if (Test-Path $cbs) {
    Get-Content $cbs -Tail 40 | ForEach-Object { $_ -replace '[^\x20-\x7E]','.' }
}

Write-Output '=== Setup events (obnovleniya) ==='
$evtSetup = "$root\Windows\System32\winevt\Logs\Setup.evtx"
if (Test-Path $evtSetup) {
    Get-WinEvent -FilterHashtable @{ Path = $evtSetup } -MaxEvents 25 | ForEach-Object {
        $m = ($_.Message -replace '\s+',' ')
        if ($m.Length -gt 140) { $m = $m.Substring(0,140) }
        Write-Output ("{0}  Id={1}  {2}" -f $_.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'), $_.Id, $m)
    }
}
