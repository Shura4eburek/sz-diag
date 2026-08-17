$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Лечение дефекта «вентилятор видеокарты постоянно на ~70 %» (СЗ 161190): убираем подсветочный
# LEDKeeper2 (MSI Mystic Light), который держит карту в P0 на простое.
#
# Грабля внутри граблей: выключить задачу `MSI Task Host - LEDKeeper2_Host` и службу
# `Mystic_Light_Service` — НЕ ДОСТАТОЧНО. Отключение переживает перезагрузку, но при первом
# же входе пользователя `MSI_Center_Service` включает задачу обратно (проверено на 161190:
# после ребута задача была Disabled, а в 14:30:51 — снова Running, LEDKeeper2 живой,
# карта опять в P0 и вентилятор 68 %). Поэтому гасить надо родителя — сам MSI Center.
#
# ⚠️ ЦЕНА РЕШЕНИЯ, проверяй ДО применения: вместе с MSI Center гаснет вся подсветка, которой
# он управляет. На 161190 предполагалось, что ARGB останется за платой (JRAINBOW/JARGB по
# настройкам BIOS) — **не подтвердилось**: башня ID-Cooling ARGB и корпусные вентиляторы
# подключены к плате, но светит ими именно Mystic Light, и без него подсветки нет вообще.
# Приборно это не видно никак — ARGB-ленты не отдают статуса в ОС, спрашивай мастера у машины.
# Поэтому рецепт — инструмент ДОКАЗАТЕЛЬСТВА причины, а не готовое лечение для отдачи клиенту.
#
# Прежнее состояние служб и задачи пишется в файл, откат — $Restore = $true.
# ⚠️ Бэкап пишется при КАЖДОМ запуске (бэклог п.171): второй прогон затрёт точку возврата уже
# изменённым состоянием. Перед повторным применением сохрани исходные значения отдельно.
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-p0-fix-ledkeeper.ps1
$Restore = $false   # ← true = вернуть всё как было

$state = 'C:\ProgramData\szdiag\ledkeeper-state.json'
$task = 'MSI Task Host - LEDKeeper2_Host'
# Порядок важен: сначала родитель (иначе он поднимет подсветку обратно), потом сама подсветка.
$svcs = @('MSI_Center_Service', 'MSI_Case_Service', 'Mystic_Light_Service')
$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
function Pstate { (& $smi --query-gpu=pstate,clocks.current.graphics,fan.speed,temperature.gpu --format=csv,noheader,nounits) -join '' }

if ($Restore) {
    if (-not (Test-Path $state)) { 'файла состояния нет — откатывать нечего'; return }
    $s = Get-Content $state -Raw | ConvertFrom-Json
    Enable-ScheduledTask -TaskName $s.Task -ErrorAction SilentlyContinue | Out-Null
    foreach ($x in $s.Services) {
        Set-Service -Name $x.Name -StartupType $x.StartMode -ErrorAction SilentlyContinue
        if ($x.State -eq 'Running') { Start-Service -Name $x.Name -ErrorAction SilentlyContinue }
        "   $($x.Name) -> $($x.StartMode)/$($x.State)"
    }
    'возвращено как было'
    return
}

"== до лечения: $(Pstate)"
$t = Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
$saved = foreach ($n in $svcs) {
    $sv = Get-CimInstance Win32_Service -Filter "Name='$n'" -ErrorAction SilentlyContinue
    if ($sv) { @{ Name = $n; StartMode = "$($sv.StartMode)"; State = "$($sv.State)" } }
}
$dir = Split-Path $state -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory $dir -Force | Out-Null }
@{ Task = $task; TaskWas = "$($t.State)"; Services = @($saved); Saved = (Get-Date).ToString('s') } |
    ConvertTo-Json -Depth 4 | Set-Content $state -Encoding UTF8
"состояние сохранено в $state"

foreach ($n in $svcs) {
    $sv = Get-Service -Name $n -ErrorAction SilentlyContinue
    if (-not $sv) { continue }
    try {
        Stop-Service -Name $n -Force -ErrorAction Stop
        Set-Service -Name $n -StartupType Disabled -ErrorAction Stop
        "   $n остановлена и Disabled"
    }
    catch { "   $n : $($_.Exception.Message)" }
}
if ($t) { Disable-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue | Out-Null; "   задача '$task' выключена" }
Get-Process LEDKeeper2 -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force -ErrorAction Stop; "   LEDKeeper2 pid=$($_.Id) остановлен" } catch { "   $($_.Exception.Message)" }
}

for ($i = 0; $i -lt 6; $i++) {
    Start-Sleep -Seconds 10
    $alive = if (Get-Process LEDKeeper2 -ErrorAction SilentlyContinue) { 'LEDKeeper2 ВЕРНУЛСЯ' } else { 'чисто' }
    "   +$((($i + 1) * 10))с  $(Pstate)   $alive"
}
'== ожидание: P8 / 210 МГц / вентилятор 0 % и LEDKeeper2 не возвращается'
