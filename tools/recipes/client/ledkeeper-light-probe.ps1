$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Проба «останется ли свет без LEDKeeper2» (СЗ 161190). Гасим ТОЛЬКО процесс подсветки, службы
# не трогаем — и мастер у машины смотрит глазами: горит ли башня ID-Cooling ARGB и корпусные
# вентиляторы, пока процесса нет.
#
# Зачем: ARGB-ленты не отдают своего состояния в ОС, приборно «есть свет / нет света» не видно
# никак (грабля из gpu-p0-fix-ledkeeper.ps1). Если контроллер платы продолжает светить последним
# эффектом — решение найдено: держим процесс эффектов мёртвым, свет остаётся статикой.
#
# Карта отпускает P0 с ЗАДЕРЖКОЙ в минуты (бэклог п.194), поэтому серия тянется 2 минуты,
# а вердикт всё равно только после обратного включения (gpu-p0-confirm.ps1).
#
#   szcli exec <СЗ> -f tools\recipes\client\ledkeeper-light-probe.ps1 --timeout 180

$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
function Pstate { (& $smi --query-gpu=pstate,utilization.gpu,clocks.current.graphics,clocks.current.memory,fan.speed,temperature.gpu --format=csv,noheader,nounits) -join '' }

"== до гашения: $(Pstate)"

# Чем именно LEDKeeper2 цепляет карту: в Mystic Light видеокарты в списке устройств НЕТ
# (проверено мастером у машины 25.08) — значит дело не в подсветке GPU, а в графическом
# контексте самого процесса. Модули d3d*/dxgi/nvapi в его адресном пространстве это и покажут.
$lk = Get-Process LEDKeeper2 -ErrorAction SilentlyContinue
if ($lk) {
    '== графические модули в LEDKeeper2:'
    $lk.Modules | Where-Object { $_.ModuleName -match 'd3d|dxgi|nvapi|opengl|vulkan|nvcuda' } |
        ForEach-Object { '   ' + $_.ModuleName + '  ' + $_.FileName }
}

$p = Get-Process LEDKeeper2 -ErrorAction SilentlyContinue
if ($p) {
    $p | Stop-Process -Force -ErrorAction SilentlyContinue
    "== LEDKeeper2 убит (pid $($p.Id -join ', ')) — СМОТРИ НА ПОДСВЕТКУ ПРЯМО СЕЙЧАС"
} else {
    '== LEDKeeper2 не запущен — гасить нечего'
}

for ($i = 0; $i -lt 12; $i++) {
    Start-Sleep -Seconds 10
    $alive = if (Get-Process LEDKeeper2 -ErrorAction SilentlyContinue) { 'жив (служба подняла обратно)' } else { 'мёртв' }
    "   +{0,3}с  LEDKeeper2: {1,-28} {2}" -f (($i + 1) * 10), $alive, (Pstate)
}

'== вопрос мастеру: подсветка башни и корпусных вертух — горела всё это время, погасла сразу или замерла статикой?'
