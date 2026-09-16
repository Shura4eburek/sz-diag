# Iz WinPE: polozhit updater na tom klienta i propisat ODNORAZOVYY avtozapusk agenta
# pri pervom vhode v sistemu (HKLM RunOnce v offline-kuste SOFTWARE).
#
# Zachem: posle remonta mashina uezzhaet v rebut, i SZ teryaetsya - agent nado
# podnimat rukami i vvodit nomer. RunOnce podnimaet ego sam i pod nuzhnoy SZ
# (updater probrasyvaet argumenty agentu - sm. AgentLauncher).
#
# Grabli:
#  - tolko ASCII v vyvode (beklog p.283);
#  - imya $Sz zanyato podstanovkoy szcli exec - nomer SZ prihodit imenno v $Sz,
#    poetomu zdes eto I ESt nuzhnoe znachenie (ne pereopredelyat);
#  - reg load/unload trebuet, chtoby nikto ne derzhal kust: rabotaem iz PE, sistema
#    klienta ne zapushchena;
#  - agent.exe pomechen requireAdministrator: pri vhode pod adminom budet zapros UAC.
$ErrorActionPreference = 'Continue'

$Vol     = 'C'                                  # tom klientskoy Windows
$SrcDir  = 'E:\szdiag\tools\updater'            # kuda priehal szcli push
$DestDir = "${Vol}:\SzDiag"
$Hive    = 'HKLM\SZOFFLINE'

Write-Output '=== Kopiruyu updater na tom klienta ==='
New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
Copy-Item "$SrcDir\*" $DestDir -Force -Recurse
Get-ChildItem $DestDir | ForEach-Object { Write-Output ("  {0}  {1} b" -f $_.Name, $_.Length) }

Write-Output '=== Propisyvayu RunOnce v offline-kuste SOFTWARE ==='
$softHive = "${Vol}:\Windows\System32\config\SOFTWARE"
& reg.exe load $Hive $softHive 2>&1 | ForEach-Object { $_ -replace '[^\x20-\x7E]','.' }
if ($LASTEXITCODE -ne 0) { Write-Output "REG_LOAD_FAILED=$LASTEXITCODE"; return }

$cmd = '"' + $DestDir + '\SzDiag.Updater.exe" ' + $Sz
& reg.exe add "$Hive\Microsoft\Windows\CurrentVersion\RunOnce" /v SzDiagAgent /t REG_SZ /d $cmd /f 2>&1 |
    ForEach-Object { $_ -replace '[^\x20-\x7E]','.' }
Write-Output ("REG_ADD_EXIT={0}" -f $LASTEXITCODE)

Write-Output '--- proverka zapisi ---'
& reg.exe query "$Hive\Microsoft\Windows\CurrentVersion\RunOnce" 2>&1 |
    ForEach-Object { $_ -replace '[^\x20-\x7E]','.' }

[gc]::Collect()
Start-Sleep -Seconds 2
& reg.exe unload $Hive 2>&1 | ForEach-Object { $_ -replace '[^\x20-\x7E]','.' }
Write-Output ("REG_UNLOAD_EXIT={0}" -f $LASTEXITCODE)
