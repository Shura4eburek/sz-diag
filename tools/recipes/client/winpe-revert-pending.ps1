# Otkat zavisshey tranzaktsii obnovleniy na offline-tome klienta iz WinPE.
# Povtoryaetsya vtoroy raz na odnoy mashine (160636 v iyule, 164266 sentyabr):
# LCU stavitsya, lozhitsya pending.xml na desyatki MB, pri starte ne primenyaetsya ->
# 0xc0000001 / tsikl zagruzki. Lechitsya revertpendingactions s posleduyushchim rebutom.
#
# Grabli: tolko ASCII v vyvode (beklog p.283); ne ispolzovat imya peremennoy $Sz.
$ErrorActionPreference = 'Continue'

$Vol = 'C'
$log = "X:\revert-pending.log"

$before = "${Vol}:\Windows\WinSxS\pending.xml"
if (Test-Path $before) {
    Write-Output ("pending.xml DO: {0} MB" -f [math]::Round((Get-Item $before).Length/1MB,2))
} else {
    Write-Output 'pending.xml DO: net (otkatyvat nechego?)'
}

Write-Output '=== dism /revertpendingactions ==='
$out = & dism.exe "/image:${Vol}:\" /cleanup-image /revertpendingactions /logpath:$log 2>&1
$code = $LASTEXITCODE
$out | ForEach-Object { $_ -replace '[^\x20-\x7E]','.' }
Write-Output ("DISM_EXIT={0}" -f $code)

if (Test-Path $before) {
    Write-Output ("pending.xml POSLE: {0} MB, mtime {1}" -f `
        [math]::Round((Get-Item $before).Length/1MB,2), (Get-Item $before).LastWriteTime)
} else {
    Write-Output 'pending.xml POSLE: net'
}
