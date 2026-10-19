# Nayti tom s ustanovlennoy Windows, kogda agent rabotaet iz WinPE.
# Grablya 1 (beklog p.283): kirillitsa v vyvode exec iz PE prihodit musorom -
#   zagolovki v etom retsepte tolko ASCII.
# Grablya 2: [char[]]('C'..'Z') NE rabotaet (range po strokam) - nuzhno
#   [char]'C'..[char]'Z', inache tsikl padaet i tom ne nahoditsya.
# Grablya 3: v PE bukva sistemnogo diska ne obyazatelno C: - opredelyat po
#   nalichiyu \Windows\System32\config\SYSTEM, a ne po bukve.
$ErrorActionPreference = 'SilentlyContinue'

Write-Output '=== Physical disks ==='
Get-Disk | Select-Object Number, FriendlyName, SerialNumber, HealthStatus, OperationalStatus,
    @{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}}, PartitionStyle, BusType |
    Format-Table -AutoSize | Out-String -Width 200

Write-Output '=== Volumes ==='
Get-Volume | Select-Object DriveLetter, FileSystemLabel, FileSystem, HealthStatus,
    @{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}},
    @{n='FreeGB';e={[math]::Round($_.SizeRemaining/1GB,1)}} |
    Format-Table -AutoSize | Out-String -Width 200

Write-Output '=== Windows location ==='
foreach ($c in [char]'C'..[char]'Z') {
    $l = [char]$c
    if (Test-Path "${l}:\Windows\System32\config\SYSTEM") {
        $evt = "${l}:\Windows\System32\winevt\Logs\System.evtx"
        $has = Test-Path $evt
        $sz  = if ($has) { [math]::Round((Get-Item $evt).Length/1MB,1) } else { 0 }
        Write-Output ("WINDOWS_VOLUME={0}: System.evtx={1} ({2} MB)" -f $l, $has, $sz)
        $bcd = Test-Path "${l}:\Windows\System32\config\BCD-Template"
        Write-Output ("  build: " + (Get-Item "${l}:\Windows\System32\ntoskrnl.exe").VersionInfo.ProductVersion)
        Write-Output ("  pending.xml: " + (Test-Path "${l}:\Windows\WinSxS\pending.xml"))
    }
}
