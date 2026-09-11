$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Цикл «гибернация → пробуждение по таймеру → замер» — серия обесточиваний GPU и памяти
# без человека у кнопки, когда умной розетки нет, а RTC-будильник платы даёт всего один
# старт в сутки.
#
# Грабля (163194): жалоба «при включении изображение то выводится, то нет». Прошлый заход
# в сервисе гонял стресс-тесты и закрыл «дефект не подтверждён» — дефект живёт на СТАРТЕ,
# а под нагрузкой память уже натренирована и видеотракт уже поднят. Нужен объём стартов.
#
# Что здесь честно, а что нет (не выкидывать, на этом строится вердикт):
#   - при S4 питание с GPU и модулей снимается, при резюме видеотракт инициализируется
#     заново => для симптома «нет изображения» цикл РАБОТАЕТ;
#   - тренинг памяти при резюме может браться из сохранённого контекста => для гипотезы
#     «EXPO не держится на холодную» цикл СЛАБЕЕ полного выключения. Полные cold boot
#     всё равно нужны руками (утро после ночного простоя — самый ценный случай).
#
# Побочные эффекты и откат (на клиенте не оставлять!):
#   включает гибернацию (powercfg /h on, hiberfil.sys ~16 ГБ), разрешает таймеры
#   пробуждения и ВЫКЛЮЧАЕТ fast startup — иначе обычное выключение станет гибридным и
#   перестанет быть холодным стартом. Прежние значения пишутся в hibernate-cycle-state.json
#   рядом со скриптом. Откат — тем же рецептом с $Mode = 'revert'.
#
# Запуск (переживает падение агента):
#   szcli exec 163194 -f tools\recipes\client\hibernate-cycle.ps1 --as-system --isolated --detach
# Стоп:
#   szcli exec 163194 "New-Item -ItemType File 'C:\szdiag\stop-cycle' -Force" --as-system

$ErrorActionPreference = 'Stop'

# --- параметры (правятся здесь: param() в рецептах ломает запуск через exec) ---
$Mode = 'run'          # 'run' — крутить цикл; 'revert' — вернуть питание как было и выйти
$AwakeMinutes = 6      # сколько машина живёт после пробуждения (проба + запас на агента)
$AsleepMinutes = 4     # сколько лежит в гибернации до пробуждения
$Cycles = 60           # сколько кругов сделать
$StopFlag = 'C:\szdiag\stop-cycle'
# ------------------------------------------------------------------------------

$agent = Get-Process -ErrorAction SilentlyContinue |
         Where-Object { $_.ProcessName -in @('agent', 'SzDiag.Agent') } |
         Select-Object -First 1 -ExpandProperty Path
if (-not $agent) { throw 'Не найден процесс агента — некуда писать состояние цикла' }
$dir = Join-Path (Split-Path -Parent $agent) 'tools\bootprobe'
if (-not (Test-Path $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
$statePath = Join-Path $dir 'hibernate-cycle-state.json'
$log = Join-Path $dir 'hibernate-cycle.log'
$probe = Join-Path $dir 'boot-display-probe.ps1'

$SUB_SLEEP = '238c9fa8-0aad-41ed-83f4-97be242c8f20'
$RTCWAKE = 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d'
$hiberboot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'

function Write-Log($msg) {
    $line = '{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $msg
    Add-Content -Path $log -Value $line -Encoding UTF8
    $line
}

# ---------------- откат ----------------
if ($Mode -eq 'revert') {
    if (-not (Test-Path $statePath)) { throw "Нет файла состояния $statePath — откатывать нечего" }
    $st = Get-Content -Raw -Encoding UTF8 $statePath | ConvertFrom-Json

    Unregister-ScheduledTask -TaskName 'szdiag-wake' -Confirm:$false -ErrorAction SilentlyContinue
    if (-not $st.HibernateWasOn) { powercfg /hibernate off | Out-Null }
    powercfg /SETACVALUEINDEX SCHEME_CURRENT $SUB_SLEEP $RTCWAKE $st.WakeTimers | Out-Null
    powercfg /SETACTIVE SCHEME_CURRENT | Out-Null
    Set-ItemProperty -Path $hiberboot -Name HiberbootEnabled -Value ([int]$st.HiberbootEnabled)

    $back = (Get-ItemProperty -Path $hiberboot -Name HiberbootEnabled).HiberbootEnabled
    Write-Log "откат: hibernate=$(if ($st.HibernateWasOn) { 'оставлен включённым' } else { 'выключен' }), HiberbootEnabled=$back, wake timers=$($st.WakeTimers)"
    Remove-Item $statePath -Force
    "откат выполнен, проверено фактическое состояние"
    return
}

# ---------------- подготовка ----------------
if (-not (Test-Path $statePath)) {
    $hibOn = Test-Path (Join-Path $env:SystemDrive 'hiberfil.sys')
    $wake = 0
    $q = powercfg /QUERY SCHEME_CURRENT $SUB_SLEEP $RTCWAKE 2>$null
    $m = $q | Select-String 'Current AC Power Setting Index:\s*(0x[0-9a-f]+)'
    if ($m) { $wake = [Convert]::ToInt32($m.Matches[0].Groups[1].Value, 16) }
    $hb = (Get-ItemProperty -Path $hiberboot -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
    if ($null -eq $hb) { $hb = 1 }

    [pscustomobject]@{ HibernateWasOn = $hibOn; WakeTimers = $wake; HiberbootEnabled = $hb } |
        ConvertTo-Json | Set-Content -Path $statePath -Encoding UTF8
    Write-Log "состояние до правок сохранено: hibernate=$hibOn, wakeTimers=$wake, HiberbootEnabled=$hb"
}

powercfg /hibernate on | Out-Null
powercfg /SETACVALUEINDEX SCHEME_CURRENT $SUB_SLEEP $RTCWAKE 1 | Out-Null   # разрешить таймеры пробуждения
powercfg /SETACTIVE SCHEME_CURRENT | Out-Null
# fast startup обязан остаться выключенным: иначе обычное выключение станет гибридным
# и перестанет быть холодным стартом — а именно холодный старт мы и проверяем
Set-ItemProperty -Path $hiberboot -Name HiberbootEnabled -Value 0

if (-not (Test-Path (Join-Path $env:SystemDrive 'hiberfil.sys'))) {
    throw 'hiberfil.sys не появился — гибернация не включилась, цикл бессмысленен'
}
Write-Log "подготовка: hibernate on, wake timers on, fast startup off"

# ---------------- цикл ----------------
# Процесс переживает гибернацию: состояние памяти сохраняется, Start-Sleep продолжается
# после пробуждения — поэтому цикл живёт в одном скрипте, без триггера «на резюме».
for ($i = 1; $i -le $Cycles; $i++) {
    if (Test-Path $StopFlag) { Write-Log "стоп-флаг $StopFlag — выхожу на круге $i"; break }

    if (Test-Path $probe) {
        try { & $probe | ForEach-Object { Write-Log "проба: $_" } }
        catch { Write-Log "проба упала: $($_.Exception.Message)" }
    } else {
        Write-Log "пробы нет по пути $probe — замер не сделан"
    }

    Write-Log "круг $i/${Cycles}: бодрствую $AwakeMinutes мин"
    Start-Sleep -Seconds ($AwakeMinutes * 60)
    if (Test-Path $StopFlag) { Write-Log "стоп-флаг перед сном — выхожу"; break }

    # разовый таск-будильник: само пробуждение делает планировщик (WakeToRun), действие пустое
    $at = (Get-Date).AddMinutes($AsleepMinutes)
    $act = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c exit'
    $trg = New-ScheduledTaskTrigger -Once -At $at
    $set = New-ScheduledTaskSettingsSet -WakeToRun -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    Register-ScheduledTask -TaskName 'szdiag-wake' -Action $act -Trigger $trg -Settings $set `
        -User 'SYSTEM' -RunLevel Highest -Force | Out-Null

    Write-Log "круг $i/${Cycles}: гибернация до $($at.ToString('HH:mm:ss'))"
    shutdown /h
    Start-Sleep -Seconds (($AsleepMinutes * 60) + 90)   # досыпаем уже после пробуждения
}

Write-Log 'цикл завершён'
"цикл завершён, лог: $log"
"НЕ ЗАБЫТЬ откат: тот же рецепт с `$Mode = 'revert' (вернёт гибернацию, fast startup и таймеры пробуждения)"
