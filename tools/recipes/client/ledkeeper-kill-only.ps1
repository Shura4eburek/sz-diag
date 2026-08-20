$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Погасить ТОЛЬКО LEDKeeper2, оставив службы подсветки живыми, и померить pstate (СЗ 161190).
#
# Грабля: 17.08 гасили LEDKeeper2 вместе со службами MSI_Center/Mystic_Light — карта ушла в P8,
# но и подсветка погасла целиком, а значит лечение клиенту не отдать. Не проверили промежуточный
# вариант: убить только процесс эффектов, службы оставить. Если контроллер удержит последний
# статический кадр — клиент получает и свет, и тишину. Замер здесь, свет — глазами у машины.
#
#   szcli exec <СЗ> -f tools\recipes\client\ledkeeper-kill-only.ps1

function Show-Gpu($tag) {
    $smi = 'C:\Windows\System32\nvidia-smi.exe'
    if (-not (Test-Path $smi)) { "   nvidia-smi нет"; return }
    $r = & $smi --query-gpu=pstate,utilization.gpu,clocks.gr,clocks.mem,temperature.gpu,fan.speed --format=csv,noheader
    "   {0,-8} {1}" -f $tag, ($r -join ' ')
}

'== ДО'
Show-Gpu 'сейчас'
Get-Service Mystic_Light_Service,MSI_Center_Service,MSI_Case_Service -ErrorAction SilentlyContinue |
    ForEach-Object { "   служба {0,-24} {1}" -f $_.Name, $_.Status }

$p = Get-Process LEDKeeper2 -ErrorAction SilentlyContinue
if (-not $p) { '   LEDKeeper2 не запущен — гасить нечего'; exit 0 }
"== гашу LEDKeeper2 (pid {0}), службы НЕ трогаю" -f $p.Id
Stop-Process -Id $p.Id -Force
Start-Sleep -Seconds 5

'== ПОСЛЕ (карта отпускает pstate не мгновенно, смотрим 60 с)'
for ($i = 1; $i -le 12; $i++) {
    Start-Sleep -Seconds 5
    $back = Get-Process LEDKeeper2 -ErrorAction SilentlyContinue
    $mark = '—'
    if ($back) { $mark = "LEDKeeper2 ВЕРНУЛСЯ pid $($back.Id)" }
    Show-Gpu ("{0:HH:mm:ss}" -f (Get-Date))
    if ($back) { "   $mark" }
}

'== службы после теста (должны быть живы)'
Get-Service Mystic_Light_Service,MSI_Center_Service,MSI_Case_Service -ErrorAction SilentlyContinue |
    ForEach-Object { "   {0,-24} {1} / {2}" -f $_.Name, $_.Status, $_.StartType }
