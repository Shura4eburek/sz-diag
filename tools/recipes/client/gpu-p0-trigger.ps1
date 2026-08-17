$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Кто переводит карту в P0 и держит её там: последовательное отключение кандидатов
# с замером pstate после каждого шага. Все службы поднимаются обратно в конце.
#
# Грабля (СЗ 161190): после чистой загрузки карта честно стоит в P8 (210/405 МГц, вентилятор 0 %),
# а через ~30 секунд — ещё на экране логина, без пользователя и без нагрузки — уходит в
# P0 (1792/7201) и вентилятор встаёт на 71 %. Это и есть жалоба клиента «кулери рандомно
# на максимальних обертах»: обороты законны для P0, вопрос — кто в этот P0 загоняет.
# Кандидаты — сервисы, которые стартуют примерно тогда же: NVIDIA Display Container
# (применяет профиль «Керування живленням» из панели), NVIDIA App, MSI Center.
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-p0-trigger.ps1

$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
if (-not (Test-Path $smi)) { 'nvidia-smi не найден'; return }
function Pstate { (& $smi --query-gpu=pstate,clocks.current.graphics,fan.speed,temperature.gpu --format=csv,noheader,nounits) -join '' }

'== процессы NVIDIA/MSI и когда они стартовали (сверить с моментом ухода в P0)'
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -match 'NVIDIA|NVDisplay|nvcontainer|nvsphelper|MSI|Mystic|LEDKeeper' } |
    Sort-Object StartTime | ForEach-Object {
        "   {0:HH:mm:ss}  {1,-28} pid={2}" -f $_.StartTime, $_.ProcessName, $_.Id
    }
'== время загрузки системы'
"   boot: {0:HH:mm:ss}" -f (Get-CimInstance Win32_OperatingSystem).LastBootUpTime

"== старт: $(Pstate)"

# Кандидаты в порядке подозрительности. Останавливаем по одному, каждый раз давая карте
# 20 секунд на возврат в idle: PowerMizer переоценивает состояние не мгновенно.
$cands = @(
    @{ Name = 'NVDisplay.ContainerLocalSystem'; Why = 'применяет профиль панели NVIDIA' },
    @{ Name = 'MSI_Center_Service'; Why = 'вендорский софт платы' },
    @{ Name = 'Mystic_Light_Service'; Why = 'подсветка, лезет к железу' }
)
$stopped = @()
foreach ($c in $cands) {
    $svc = Get-Service -Name $c.Name -ErrorAction SilentlyContinue
    if (-not $svc) { "   [$($c.Name)] службы нет"; continue }
    if ($svc.Status -ne 'Running') { "   [$($c.Name)] не запущена"; continue }
    "-- стоп $($c.Name) ($($c.Why))"
    try {
        Stop-Service -Name $c.Name -Force -ErrorAction Stop
        $stopped += $c.Name
    }
    catch { "   не остановилась: $($_.Exception.Message)"; continue }
    for ($i = 0; $i -lt 4; $i++) { Start-Sleep -Seconds 5; "   +$((($i + 1) * 5))с  $(Pstate)" }
}

'== поднимаем всё обратно'
foreach ($n in $stopped) {
    try { Start-Service -Name $n -ErrorAction Stop; "   $n запущена" }
    catch { "   $n НЕ поднялась: $($_.Exception.Message) — поднять вручную!" }
}
Start-Sleep -Seconds 10
"== после возврата служб: $(Pstate)"
