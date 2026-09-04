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
# #113 / б.171 (161190): бэкап пишется РОВНО ОДИН РАЗ. Раньше он перезаписывался при КАЖДОМ
# запуске — второй прогон (доработка лечения) сохранял состояние, которое УЖЕ было изменено
# первым прогоном (`Mystic_Light_Service` = Disabled/Stopped вместо исходного Auto/Running),
# и `$Restore` потом «возвращал» подсветку в уже поломанное состояние, честно считая его
# исходным. Если файл уже есть — он не трогается, и это явно сказано в выводе.
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-p0-fix-ledkeeper.ps1
$Restore = $false   # ← true = вернуть всё как было

$state = 'C:\ProgramData\szdiag\ledkeeper-state.json'
$task = 'MSI Task Host - LEDKeeper2_Host'
# Порядок важен: сначала родитель (иначе он поднимет подсветку обратно), потом сама подсветка.
$svcs = @('MSI_Center_Service', 'MSI_Case_Service', 'Mystic_Light_Service')
$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
function Pstate { (& $smi --query-gpu=pstate,clocks.current.graphics,fan.speed,temperature.gpu --format=csv,noheader,nounits) -join '' }

# R-I1 (ревью волны 1): StartMode из Win32_Service ('Auto'/'Manual'/'Disabled'/'Boot'/'System')
# и -StartupType у Set-Service ('Automatic'/'Manual'/'Disabled'/…) — РАЗНЫЕ словари. Подстановка
# StartMode как есть (Set-Service -StartupType Auto) валится ошибкой привязки параметра, и
# точка возврата (#113) молча не возвращает подсветку — цена «не перезаписываем бэкап» тогда
# обнуляется тем, что откат из него не работает вообще.
function ConvertTo-StartupType($startMode) {
    switch ($startMode) {
        'Auto' { 'Automatic' }
        default { $startMode }   # Manual/Disabled совпадают дословно; Boot/System у обычных служб не встречаются
    }
}

if ($Restore) {
    if (-not (Test-Path $state)) { 'файла состояния нет — откатывать нечего'; return }
    $s = Get-Content $state -Raw | ConvertFrom-Json
    # Печатаем, ЧТО будем возвращать, ДО применения (#113 / б.171) — если точка возврата
    # когда-то сохранилась уже поломанной, это должно быть видно СРАЗУ, а не задним числом.
    "== возвращаем состояние, сохранённое $($s.Saved):"
    "   задача '$($s.Task)': была $($s.TaskWas)"
    foreach ($x in $s.Services) { "   $($x.Name) -> $($x.StartMode)/$($x.State)" }
    ''
    # R-M10 (ревью волны 1): без -TaskPath Enable-/Disable-ScheduledTask подразумевают корень
    # '\' — задача из подпапки находится в Get-ScheduledTask (по имени), но не переключается.
    # $s.TaskPath может отсутствовать в старой точке возврата — фоллбэк на корень.
    $taskPath = if ($s.TaskPath) { $s.TaskPath } else { '\' }
    # I-16 (ревью волны 2): раньше задача включалась безусловно — если машина приехала с уже
    # ВЫКЛЮЧЕННОЙ задачей (TaskWas = Disabled), откат оставлял её включённой, то есть точка
    # возврата возвращала НЕ то состояние, которое было исходно.
    if ($s.TaskWas -eq 'Disabled') {
        Disable-ScheduledTask -TaskName $s.Task -TaskPath $taskPath -ErrorAction SilentlyContinue | Out-Null
    } else {
        Enable-ScheduledTask -TaskName $s.Task -TaskPath $taskPath -ErrorAction SilentlyContinue | Out-Null
    }
    $anyMismatch = $false
    foreach ($x in $s.Services) {
        Set-Service -Name $x.Name -StartupType (ConvertTo-StartupType $x.StartMode) -ErrorAction SilentlyContinue
        if ($x.State -eq 'Running') { Start-Service -Name $x.Name -ErrorAction SilentlyContinue }
        # I-17 (ревью волны 2): и Set-Service, и Start-Service шли с -ErrorAction
        # SilentlyContinue, а строка ниже эхом печатала ЖЕЛАЕМОЕ состояние независимо от
        # исхода — «возвращено как было» держалось даже когда проглоченная ошибка привязки
        # (пустой/непривычный StartMode в старой точке возврата) ничего не поменяла на самом
        # деле. Перечитываем фактическое состояние службы и печатаем его, а не намерение.
        $actual = Get-CimInstance Win32_Service -Filter "Name='$($x.Name)'" -ErrorAction SilentlyContinue
        $actualMode = if ($actual) { "$($actual.StartMode)" } else { '?' }
        $actualState = if ($actual) { "$($actual.State)" } else { '?' }
        $mismatch = ($actualMode -ne $x.StartMode) -or ($actualState -ne $x.State)
        if ($mismatch) { $anyMismatch = $true }
        $flag = if ($mismatch) { ' !!! НЕ ВЕРНУЛОСЬ' } else { '' }
        "   $($x.Name): хотели $($x.StartMode)/$($x.State), сейчас $actualMode/$actualState$flag"
    }
    if ($anyMismatch) { '!!! возвращено НЕ ВСЁ как было — смотри пометки выше' } else { 'возвращено как было' }
    return
}

"== до лечения: $(Pstate)"
# Minor (ревью волны 2): без -First 1 задача с таким именем в НЕСКОЛЬКИХ папках даёт массив,
# и $t.TaskPath дальше превращается в массив строк — привязка параметра -TaskPath у
# Disable-ScheduledTask на массиве непредсказуема.
$t = Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue | Select-Object -First 1

# #113 / б.171: бэкап пишется РОВНО ОДИН РАЗ за жизнь точки возврата. Повторный запуск
# (доработка лечения) видит уже применённые изменения — перезаписать файл ими означало бы
# заменить "исходное" состояние на "уже поломанное" (см. заголовок файла).
if (Test-Path $state) {
    "состояние уже сохранено ранее ($state) — НЕ перезаписываю, использую как точку возврата"
    $existing = Get-Content $state -Raw | ConvertFrom-Json
    "   (сохранено $($existing.Saved), задача была $($existing.TaskWas))"
}
else {
    $saved = foreach ($n in $svcs) {
        $sv = Get-CimInstance Win32_Service -Filter "Name='$n'" -ErrorAction SilentlyContinue
        if ($sv) { @{ Name = $n; StartMode = "$($sv.StartMode)"; State = "$($sv.State)" } }
    }
    $dir = Split-Path $state -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory $dir -Force | Out-Null }
    @{ Task = $task; TaskWas = "$($t.State)"; TaskPath = "$(if ($t) { $t.TaskPath } else { '\' })";
       Services = @($saved); Saved = (Get-Date).ToString('s') } |
        ConvertTo-Json -Depth 4 | Set-Content $state -Encoding UTF8
    "состояние сохранено в $state"
}

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
if ($t) { Disable-ScheduledTask -TaskName $task -TaskPath $t.TaskPath -ErrorAction SilentlyContinue | Out-Null; "   задача '$task' выключена" }
Get-Process LEDKeeper2 -ErrorAction SilentlyContinue | ForEach-Object {
    try { Stop-Process -Id $_.Id -Force -ErrorAction Stop; "   LEDKeeper2 pid=$($_.Id) остановлен" } catch { "   $($_.Exception.Message)" }
}

for ($i = 0; $i -lt 6; $i++) {
    Start-Sleep -Seconds 10
    $alive = if (Get-Process LEDKeeper2 -ErrorAction SilentlyContinue) { 'LEDKeeper2 ВЕРНУЛСЯ' } else { 'чисто' }
    "   +$((($i + 1) * 10))с  $(Pstate)   $alive"
}
'== ожидание: P8 / 210 МГц / вентилятор 0 % и LEDKeeper2 не возвращается'
