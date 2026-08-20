$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Второй подозреваемый после LEDKeeper2: оболочка MSI Center 2.0.73.0 (DCv2.exe) — она грузит
# d3d9.dll/nvd3dumx.dll, то есть рендерит через видеокарту и сама может держать P0 (СЗ 161190).
#
# Грабля: до обновления оболочки в автозапуске не было вовсе (Appx-пакета не существовало),
# и P0 держал только LEDKeeper2. После апдейта подозреваемых стало двое — гасим по одному
# с замером, иначе вердикт «обновление не помогло» смешает две разные причины.
#
#   szcli exec <СЗ> -f tools\recipes\client\dcv2-kill-probe.ps1

function Show-Gpu($tag) {
    $r = & 'C:\Windows\System32\nvidia-smi.exe' --query-gpu=pstate,utilization.gpu,clocks.gr,clocks.mem,temperature.gpu,fan.speed --format=csv,noheader
    "   {0,-9} {1}" -f $tag, ($r -join ' ')
}

'== состояние сейчас (LEDKeeper2 гашен ранее)'
Show-Gpu 'до'
Get-Process LEDKeeper2,DCv2 -ErrorAction SilentlyContinue | ForEach-Object { "   живой: {0} pid {1}" -f $_.Name, $_.Id }

$d = Get-Process DCv2 -ErrorAction SilentlyContinue
if ($d) {
    "== гашу DCv2 (pid {0})" -f $d.Id
    $d | Stop-Process -Force
} else { '== DCv2 не запущен' }

for ($i = 1; $i -le 12; $i++) {
    Start-Sleep -Seconds 5
    Show-Gpu ("{0:HH:mm:ss}" -f (Get-Date))
}

'== кто из подозреваемых вернулся'
$alive = Get-Process LEDKeeper2,DCv2 -ErrorAction SilentlyContinue
if ($alive) { $alive | ForEach-Object { "   {0} pid {1} старт {2:HH:mm:ss}" -f $_.Name, $_.Id, $_.StartTime } } else { '   ни одного' }
