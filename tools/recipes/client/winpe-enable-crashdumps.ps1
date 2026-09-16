# Iz WinPE: proverit i vklyuchit zapis dampov BSOD v offline-kuste SYSTEM klienta.
# Povod (164266): klient zhaluetsya na "siniy ekran", a Minidump\ pust i MEMORY.DMP net
# za polтора mesyatsa - libo dampy vyklyucheny, libo eto ne BSOD voobshche.
# Bez dampov razbor BSOD nevozmozhen, a vtorogo vizita mozhet ne byt.
#
# CrashDumpEnabled: 0 net, 1 complete, 2 kernel, 3 small(mini), 7 automatic.
# Stavim 2 (kernel) + AlwaysKeepMemoryDump=1, chtoby damp ne chistilsya avtomaticheski.
# Grabli: tolko ASCII; imya $Sz zanyato podstanovkoy szcli exec.
$ErrorActionPreference = 'Continue'

$Vol  = 'C'
$Hive = 'HKLM\SZSYS'

& reg.exe load $Hive "${Vol}:\Windows\System32\config\SYSTEM" 2>&1 | ForEach-Object { $_ -replace '[^\x20-\x7E]','.' }
if ($LASTEXITCODE -ne 0) { Write-Output "REG_LOAD_FAILED=$LASTEXITCODE"; return }

# Kakoy ControlSet aktivnyy - beryom iz Select\Current.
$cur = (Get-ItemProperty "HKLM:\SZSYS\Select" -Name Current).Current
$cs  = 'ControlSet{0:d3}' -f $cur
Write-Output ("Aktivnyy nabor: {0}" -f $cs)

$cc = "HKLM:\SZSYS\$cs\Control\CrashControl"
Write-Output '=== CrashControl DO ==='
Get-ItemProperty $cc | Select-Object CrashDumpEnabled, AutoReboot, DumpFile, MinidumpDir,
    MinidumpsCount, AlwaysKeepMemoryDump, LogEvent, Overwrite | Format-List | Out-String -Width 120

Set-ItemProperty $cc -Name CrashDumpEnabled -Value 2 -Type DWord
Set-ItemProperty $cc -Name AlwaysKeepMemoryDump -Value 1 -Type DWord
Set-ItemProperty $cc -Name AutoReboot -Value 1 -Type DWord

Write-Output '=== CrashControl POSLE ==='
Get-ItemProperty $cc | Select-Object CrashDumpEnabled, AutoReboot, DumpFile, MinidumpDir,
    AlwaysKeepMemoryDump | Format-List | Out-String -Width 120

# Zaodno: est li fayl podkachki na sistemnom tome - bez nego yadro damp ne zapishet.
$pf = (Get-ItemProperty "HKLM:\SZSYS\$cs\Control\Session Manager\Memory Management" -Name PagingFiles -ErrorAction SilentlyContinue).PagingFiles
Write-Output ("PagingFiles: " + ($pf -join ' | '))

[gc]::Collect()
Start-Sleep -Seconds 2
& reg.exe unload $Hive 2>&1 | ForEach-Object { $_ -replace '[^\x20-\x7E]','.' }
Write-Output ("REG_UNLOAD_EXIT={0}" -f $LASTEXITCODE)
