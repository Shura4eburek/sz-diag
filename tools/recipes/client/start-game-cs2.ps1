$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Запуск CS2 как НАГРУЗОЧНОГО прогона: бой с ботами, без человека за клавиатурой.
# Нужен там, где жалоба звучит как «вимикається в грі»: синтетика (OCCT) на 160705 молчала
# четыре прогона подряд, а реальная игра убила карту за 2 мин 14 с при МЕНЬШИХ средних
# (76 % / 134 Вт против 100 % / 197 Вт в OCCT). Характер нагрузки решает, а не её величина.
#
# Грабли, зашитые сюда (СЗ 160705, 12.08.2026):
#  1. `+map de_dust2` БЕЗ `+game_type 0 +game_mode 1` карту не поднимает — игра встаёт в главном
#     меню, а GPU показывает ~30 % / 42 Вт. Это легко принять за «прогон идёт» и потерять часы.
#  2. CS2 душит рендер, когда окно не в фокусе → `+engine_no_focus_sleep 0`, иначе прогон без
#     человека у машины идёт вполсилы.
#  3. Лимит кадров убирается только `+fps_max 0`: у клиента лимита не было, воспроизводить надо
#     его условия.
#  4. `schtasks /tr` с путём в пробелах через szcli exec ломается → Register-ScheduledTask.
#  5. Игра — GUI-процесс: только интерактивная задача под сеансом пользователя (LogonType
#     Interactive), из сессии агента/SYSTEM не поднимется вообще.
#  6. Приёмка — ТОЛЬКО по сенсорам: «cs2.exe жив» не значит «карта под нагрузкой».
#  7. Steam молча качает апдейт игры — и `-applaunch` тогда НЕ поднимает cs2.exe вообще
#     (СЗ 160705, 19.08.2026: ждали 120 с, писали «НЕ ПОДНЯЛСЯ», потеряли вечер; на деле шла
#     закачка 2,2 ГБ на 9 Мбит/с ≈ 35 мин). Состояние — в appmanifest_730.acf: StateFlags 4 =
#     установлена, всё прочее (2/1030/…) = обновление/докачка. ВНИМАНИЕ: BytesDownloaded в acf
#     обновляется редко и в простое выглядит замершим — идёт ли загрузка, видно только по
#     `logs\content_log.txt` (строки 'Current download rate').
#
# Перед запуском: start-sensors.ps1 (приборка обязательна — иначе отказ нечем разбирать,
# бэклог п.137) и, если нужен режим клиента, apply-ab-profile.ps1.
#   szcli exec <СЗ> -f tools\recipes\client\start-game-cs2.ps1
$Sz   = '000000'      # ← номер СЗ
$Map  = 'de_dust2'
$Bots = 10

$steam  = 'C:\Program Files (x86)\Steam\steam.exe'
$gameEx = 'C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe'
$cfgDir = 'C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg'
$Task   = "szdiag-game-$Sz"

if (-not (Test-Path $steam))  { throw "Steam не найден: $steam" }
if (-not (Test-Path $gameEx)) { throw "CS2 не установлена: $gameEx" }
if (-not (Test-Path $cfgDir)) { throw "нет папки конфигов: $cfgDir" }

# Дезматч с респавном и без условий конца раунда: нагрузка держится ровно, никто не «выигрывает»
# и не выкидывает в лобби посреди прогона.
$body = @"
fps_max 0
mp_autoteambalance 0
mp_limitteams 0
mp_freezetime 0
mp_warmuptime 5
mp_roundtime 60
mp_roundtime_defuse 60
mp_ignore_round_win_conditions 1
mp_respawn_on_death_ct 1
mp_respawn_on_death_t 1
bot_difficulty 2
bot_quota_mode fill
bot_quota $Bots
mp_restartgame 1
"@
Set-Content -Path (Join-Path $cfgDir 'szdiag.cfg') -Value $body -Encoding Ascii
('конфиг записан: ' + (Join-Path $cfgDir 'szdiag.cfg'))

$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
if (-not $expl) { throw 'нет активного сеанса — игру запускать некому' }
$o = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
$user = $o.Domain + '\' + $o.User
('сеанс: ' + $user)

# Игра не в состоянии «установлена» => запускать нечего: Steam уйдёт качать, а мы будем
# 120 секунд смотреть на «НЕ ПОДНЯЛСЯ» и искать дефект там, где его нет.
$acf = 'C:\Program Files (x86)\Steam\steamapps\appmanifest_730.acf'
if (Test-Path $acf) {
    $flags = [int](((Get-Content $acf | Select-String '"StateFlags"').Line -replace '\D', ''))
    if ($flags -ne 4) {
        $done  = [int64](((Get-Content $acf | Select-String '"BytesDownloaded"').Line -replace '\D', ''))
        $total = [int64](((Get-Content $acf | Select-String '"BytesToDownload"').Line -replace '\D', ''))
        $rate  = (Get-Content 'C:\Program Files (x86)\Steam\logs\content_log.txt' -Tail 400 -ErrorAction SilentlyContinue |
                  Select-String 'Current download rate' | Select-Object -Last 1).Line
        throw ("CS2 НЕ ГОТОВА: StateFlags=$flags (4 = установлена). Идёт обновление: " +
               ('{0:N0} из {1:N0} МБ' -f ($done/1MB), ($total/1MB)) +
               $(if ($rate) { '. ' + $rate.Trim() } else { '. скорости в content_log нет — загрузка может стоять' }) +
               '. Дождись StateFlags=4 и запусти прогон заново.')
    }
} else { 'ВНИМАНИЕ: appmanifest_730.acf не найден — состояние установки не проверено' }

Get-Process cs2 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 5

# `-condebug` ДОПИСЫВАЕТ в console.log, поэтому строки про ботов от прошлого прогона живут в
# файле вечно и засчитываются как «матч идёт» (СЗ 160705, 19.08.2026: игра не поднялась вообще,
# а рецепт напечатал «ботов в матче: 9» — с прогона недельной давности). Убираем файл до старта.
$log = 'C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\console.log'
if (Test-Path $log) {
    $bak = $log -replace '\.log$', ('-' + (Get-Date -Format 'MMdd-HHmmss') + '.log')
    Move-Item $log $bak -Force
    ('прошлый console.log убран в ' + (Split-Path $bak -Leaf))
}

$gameArgs = "-applaunch 730 -novid -condebug -fullscreen +engine_no_focus_sleep 0 +fps_max 0 +game_type 0 +game_mode 1 +sv_lan 1 +map $Map +exec szdiag"
try { Unregister-ScheduledTask -TaskName $Task -Confirm:$false -ErrorAction Stop } catch { }
$action    = New-ScheduledTaskAction -Execute $steam -Argument $gameArgs
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $Task -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $Task
('СТАРТ: ' + (Get-Date).ToString('HH:mm:ss') + '  steam.exe ' + $gameArgs)

Start-Sleep -Seconds 120
$g = Get-Process cs2 -ErrorAction SilentlyContinue
('cs2.exe: ' + $(if ($g) { 'жив, pid ' + $g.Id + ', RAM ' + [int]($g.WorkingSet64/1MB) + ' МБ' } else { 'НЕ ПОДНЯЛСЯ' }))

# Карта реально загрузилась? Строки про ботов в console.log — единственное надёжное
# подтверждение, что мы не стоим в меню.
$log = 'C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\console.log'
if (Test-Path $log) {
    $bots = @(Select-String -Path $log -Pattern 'BOT.*ChangeTeam' -ErrorAction SilentlyContinue).Count
    ('ботов в матче (по console.log): ' + $bots + $(if ($bots -eq 0) { '  ← КАРТА НЕ ЗАГРУЗИЛАСЬ, игра в меню' } else { '' }))
    if (-not $g -and $bots -gt 0) {
        'ОШИБКА: процесса cs2 нет, но боты в логе есть — значит лог не от этого прогона. Прогон НЕ засчитывать.'
    }
} else { 'console.log не создан — игра не стартовала (проверь параметры запуска Steam)' }
'приёмка нагрузки — game-load-check.ps1 (GPU >= 60 % большую часть замеров)'
