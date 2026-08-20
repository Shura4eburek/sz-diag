$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Чистый тест (СЗ 161190): MSI Center и подсветка ЖИВЫЕ, uTorrent мёртвый — держит ли карта P8?
#
# Грабля: поиск держателя P0 гасил кандидатов по очереди, и к моменту uTorrent весь MSI-софт
# уже лежал — значит «отпустил uTorrent» может быть совпадением с отложенным отпусканием.
# Дискриминатор: поднять MSI обратно и мерить. P8 при живом LEDKeeper2 = виноват торрент,
# и тогда подсветку клиенту трогать вообще не надо.
#
#   szcli exec <СЗ> -f tools\recipes\client\msi-restore-utorrent-off.ps1

function Get-PState {
    $r = & 'C:\Windows\System32\nvidia-smi.exe' --query-gpu=pstate,utilization.gpu,clocks.gr,fan.speed,temperature.gpu --format=csv,noheader
    ($r -join ' ')
}

'== до восстановления'
"   " + (Get-PState)

'== поднимаю службы MSI обратно'
foreach ($n in 'MSI_Center_Service','MSI_Case_Service','Mystic_Light_Service','LightKeeperService') {
    $svc = Get-Service $n -ErrorAction SilentlyContinue
    if (-not $svc) { "   {0,-24} нет такой" -f $n; continue }
    if ($svc.StartType -eq 'Disabled') { Set-Service $n -StartupType Automatic }
    Start-Service $n -ErrorAction SilentlyContinue
    $svc.Refresh()
    "   {0,-24} {1} / {2}" -f $svc.Name, $svc.Status, $svc.StartType
}

'== жду, пока MSI_Center_Service поднимет подсветку (LEDKeeper2)'
for ($i = 1; $i -le 24; $i++) {
    Start-Sleep -Seconds 5
    $led = Get-Process LEDKeeper2 -ErrorAction SilentlyContinue
    $ut  = Get-Process uTorrentClients,uTorrent,uTorrentie -ErrorAction SilentlyContinue
    $ledTxt = 'нет'
    if ($led) { $ledTxt = "pid $($led[0].Id)" }
    $utTxt = 'нет'
    if ($ut) { $utTxt = ($ut | Select-Object -Expand Id) -join ',' }
    Write-Output ("   {0:HH:mm:ss} {1} | LEDKeeper2: {2} | uTorrent: {3}" -f (Get-Date), (Get-PState), $ledTxt, $utTxt)
}

'== итог'
"   pstate: " + (Get-PState)
Get-Service MSI_Center_Service,MSI_Case_Service,Mystic_Light_Service,LightKeeperService -ErrorAction SilentlyContinue |
    ForEach-Object { "   {0,-24} {1} / {2}" -f $_.Name, $_.Status, $_.StartType }
