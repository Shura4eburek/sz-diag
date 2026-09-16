# Offline-triazh klientskoy Windows iz WinPE: pochemu ne gruzitsya + istoriya vyrubonov.
# Chitaem EVTX i fayly na tome klienta (bez zapusti ego OS).
#
# Grabli:
#  - kirillitsa v vyvode exec iz PE prihodit musorom (beklog p.283) -> tolko ASCII;
#  - imya peremennoy $Sz zanyato: szcli exec vsegda podstavlyaet nomer SZ v $Sz
#    (ExecParams.WithAutoSz, IgnoreCase) - lyubaya svoya $sz budet zatyorta nomerom;
#  - Get-WinEvent -Path v PE rabotaet, no trebuet polnogo puti k .evtx.
$ErrorActionPreference = 'SilentlyContinue'

$Vol = 'C'        # tom klientskoy Windows (naiden winpe-find-windows-volume.ps1)
$root = "${Vol}:"
$evtSys = "$root\Windows\System32\winevt\Logs\System.evtx"

Write-Output '=== Boot blockers ==='
$p = "$root\Windows\WinSxS\pending.xml"
if (Test-Path $p) {
    $mb = [math]::Round((Get-Item $p).Length/1MB,2)
    Write-Output ("pending.xml: {0} MB, mtime {1}" -f $mb, (Get-Item $p).LastWriteTime)
} else { Write-Output 'pending.xml: net' }
foreach ($f in 'pending.xml.bad','poqexec.log') {
    $q = "$root\Windows\WinSxS\$f"
    if (Test-Path $q) { Write-Output ("{0}: est ({1})" -f $f, (Get-Item $q).LastWriteTime) }
}
$cbs = "$root\Windows\Logs\CBS\CBS.log"
if (Test-Path $cbs) { Write-Output ("CBS.log mtime: " + (Get-Item $cbs).LastWriteTime) }

Write-Output '=== Minidumps / MEMORY.DMP ==='
Get-ChildItem "$root\Windows\Minidump\*.dmp" | Sort-Object LastWriteTime -Descending |
    Select-Object -First 15 Name, LastWriteTime, @{n='KB';e={[math]::Round($_.Length/1KB)}} |
    Format-Table -AutoSize | Out-String -Width 160
$mem = "$root\Windows\MEMORY.DMP"
if (Test-Path $mem) {
    Write-Output ("MEMORY.DMP: {0} MB, {1}" -f [math]::Round((Get-Item $mem).Length/1MB), (Get-Item $mem).LastWriteTime)
} else { Write-Output 'MEMORY.DMP: net' }
Write-Output '=== LiveKernelReports ==='
(Get-ChildItem "$root\Windows\LiveKernelReports\*.dmp" -Recurse).Count

Write-Output '=== Kernel-Power 41 / 6008 / BugCheck (offline System.evtx) ==='
if (Test-Path $evtSys) {
    $ev = Get-WinEvent -FilterHashtable @{ Path = $evtSys; Id = 41,1001,6008,6006 } -MaxEvents 400
    Write-Output ("vsego sobytiy: " + $ev.Count)
    foreach ($e in $ev) {
        if ($e.Id -eq 41) {
            $x = [xml]$e.ToXml()
            $d = @{}
            foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
            Write-Output ("{0}  41 KernelPower  bugcheck={1} button={2} sleep={3}" -f `
                $e.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'), $d['BugcheckCode'], $d['PowerButtonTimestamp'], $d['SleepInProgress'])
        } elseif ($e.Id -eq 1001) {
            Write-Output ("{0}  1001 BugCheck   {1}" -f $e.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'), ($e.Message -replace '\s+',' ').Substring(0,[math]::Min(150,($e.Message -replace '\s+',' ').Length)))
        } else {
            Write-Output ("{0}  {1}" -f $e.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'), $e.Id)
        }
    }
} else { Write-Output "NET FAYLA: $evtSys" }
