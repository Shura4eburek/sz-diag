$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Обратный тест к gpu-p0-suspects.ps1: возвращаем подозреваемый процесс и смотрим, вернётся ли
# карта в P0. Без этого шага вывод «виноват вот этот процесс» держится на одном совпадении —
# гасили-то несколько подряд.
#
# Грабля (СЗ 161190): после гашения LEDKeeper2 (подсветка MSI Mystic Light) карта ушла в P8
# и вентилятор упал с 71 % до нуля — но следом гасились ещё два процесса, так что «убрал —
# прошло» само по себе не доказательство. Доказательство — «вернул — вернулось».
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-p0-confirm.ps1

$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
function Pstate { (& $smi --query-gpu=pstate,utilization.gpu,clocks.current.graphics,fan.speed,temperature.gpu --format=csv,noheader,nounits) -join '' }

"== до возврата: $(Pstate)"
'== процесс LEDKeeper2 сейчас: ' + $(if (Get-Process LEDKeeper2 -ErrorAction SilentlyContinue) { 'жив' } else { 'не запущен' })

# LEDKeeper2 живёт в сессии пользователя и поднимается службой подсветки — перезапуск службы
# возвращает его штатным путём, без запуска exe руками из-под SYSTEM.
'-- перезапуск Mystic_Light_Service'
Restart-Service -Name Mystic_Light_Service -Force -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 6; $i++) {
    Start-Sleep -Seconds 10
    $p = Get-Process LEDKeeper2 -ErrorAction SilentlyContinue
    "   +$((($i + 1) * 10))с  LEDKeeper2: {0,-12} {1}" -f $(if ($p) { "жив pid=$($p.Id)" } else { 'нет' }), (Pstate)
}
'== вывод: если с возвращением LEDKeeper2 карта снова ушла в P0 — связь подтверждена'
