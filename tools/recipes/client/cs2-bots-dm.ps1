$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# CS2 как нагрузка «как у клиента»: Deathmatch с ботами, бесконечный раунд, рандомный респавн,
# игрок — в спектаторах (машину никто не трогает, а боты грузят CPU+GPU как в живой игре).
#
# Зачем (168175, 09.11): FurMark 60 мин чисто (ровные 360 Вт), а клиент ловит 0x116 в CS2 —
# там CPU+GPU вместе и рваное потребление. Удалённо в консоль игры не попасть, поэтому:
#  - настройки сервера кладём в gamemode_deathmatch_server.cfg (CS2 выполняет его ПОСЛЕ
#    gamemode_deathmatch.cfg при загрузке карты в DM) — autoexec.cfg/config.cfg клиента не трогаем;
#  - CS2 перезапускается задачей в сессии пользователя с -netconport, и уже по netcon (TCP на
#    127.0.0.1) шлём `jointeam 1` — это клиентская команда, из server.cfg её не выполнить.
# ⚠️ 168175, 09.11: аргументы дошли (`-netconport 2121` в командной строке cs2), но порт так и не
#    открылся (cs2 слушала только 127.0.0.1:<случайный>) — netcon в этой сборке CS2 не поднялся.
#    Шаг `jointeam 1` тогда — руками у машины (M → Наблюдатель или консоль `jointeam 1`).
# ⚠️ 168175, 09.11: после ребута Steam был запущен, но НЕ залогинен — `-applaunch` молча ничего не делает
#    (задача SUCCESS, cs2 нет). Перед запуском — убедиться, что в Steam выполнен вход.
# Откат: -Cleanup 1 удаляет наш cfg (если до нас такого файла не было).
#
#   szcli exec <СЗ> -f tools\recipes\client\cs2-bots-dm.ps1 --timeout 240
#   szcli exec <СЗ> -f tools\recipes\client\cs2-bots-dm.ps1 --param Cleanup=1
$Sz       = '168175'
$Map      = 'de_dust2'
$Bots     = 16
$Diff     = 3        # 0 easy … 3 expert
$Port     = 2121
$Cleanup  = 0

$steamDir = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -EA SilentlyContinue).InstallPath
$vdf = Join-Path $steamDir 'steamapps\libraryfolders.vdf'
$libs = @($steamDir) + @([regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' })
$cfgDir = $null
foreach ($l in ($libs | Select-Object -Unique)) {
    $c = Join-Path $l 'steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg'
    if (Test-Path $c) { $cfgDir = $c; break }
}
if (-not $cfgDir) { 'НЕТ папки cfg CS2'; return }
$ours = Join-Path $cfgDir 'gamemode_deathmatch_server.cfg'
$mark = '// szdiag ' + $Sz

if ($Cleanup -eq 1) {
    if ((Test-Path $ours) -and ((Get-Content $ours -TotalCount 1) -eq $mark)) { Remove-Item $ours -Force; "удалён $ours" }
    else { "наш cfg не найден или файл чужой — не трогаю: $ours" }
    return
}

if ((Test-Path $ours) -and ((Get-Content $ours -TotalCount 1) -ne $mark)) { "у клиента уже есть свой $ours — не перезаписываю, стоп"; return }
$cfg = @"
$mark
mp_teammates_are_enemies 1
mp_randomspawn 1
mp_respawn_on_death_t 1
mp_respawn_on_death_ct 1
mp_ignore_round_win_conditions 1
mp_roundtime 60
mp_timelimit 0
mp_maxrounds 0
mp_do_warmup_period 0
mp_warmuptime 0
mp_autoteambalance 0
mp_limitteams 0
mp_forcecamera 0
bot_difficulty $Diff
bot_quota_mode normal
bot_quota $Bots
bot_join_after_player 0
bot_dont_shoot 0
"@
[IO.File]::WriteAllText($ours, $cfg, (New-Object Text.UTF8Encoding($false)))
"cfg записан: $ours"

$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
if (-not $expl) { 'НЕТ живой сессии'; return }
$owner = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
$sid = (New-Object Security.Principal.NTAccount($owner.Domain, $owner.User)).Translate([Security.Principal.SecurityIdentifier]).Value

$cs = Get-Process cs2 -EA SilentlyContinue
if ($cs) {
    'закрываю запущенную cs2 (pid ' + ($cs.Id -join ',') + ')'
    $cs | ForEach-Object { [void]$_.CloseMainWindow() }
    for ($i = 0; $i -lt 20 -and (Get-Process cs2 -EA SilentlyContinue); $i++) { Start-Sleep 1 }
    Get-Process cs2 -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep 3
}

$steam = Join-Path $steamDir 'steam.exe'
$a = "-applaunch 730 -novid -console -netconport $Port +game_type 1 +game_mode 2 +map $Map"
$task = "szdiag-cs2-$Sz"
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Principals><Principal id="Author"><UserId>$sid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>
  <Actions Context="Author"><Exec><Command>$steam</Command><Arguments>$a</Arguments></Exec></Actions>
</Task>
"@
$tmp = Join-Path $env:TEMP "$task.xml"
[IO.File]::WriteAllText($tmp, $xml, [Text.Encoding]::Unicode)
schtasks /delete /tn $task /f 2>$null | Out-Null
'create: ' + (schtasks /create /tn $task /xml $tmp /f 2>&1)
'run:    ' + (schtasks /run /tn $task 2>&1)
Remove-Item $tmp -Force -EA SilentlyContinue

# Ждём cs2 и открытый netcon-порт
$ok = $false
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep 2
    if (Get-NetTCPConnection -LocalPort $Port -State Listen -EA SilentlyContinue) { $ok = $true; break }
}
$p = Get-Process cs2 -EA SilentlyContinue
'cs2: ' + $(if ($p) { 'жив pid=' + $p.Id } else { 'НЕ ЗАПУСТИЛАСЬ' }) + ', netcon ' + $Port + ': ' + $(if ($ok) { 'слушает' } else { 'НЕТ' })
if (-not $ok) { 'netcon не поднялся — в спектаторы вручную: M → Наблюдатель (или консоль: jointeam 1)'; return }

function Send-Netcon([string[]]$cmds, [int]$wait = 2) {
    $c = New-Object Net.Sockets.TcpClient('127.0.0.1', $Port)
    $s = $c.GetStream(); $w = New-Object IO.StreamWriter($s); $w.NewLine = "`n"; $w.AutoFlush = $true
    foreach ($x in $cmds) { $w.WriteLine($x); Start-Sleep -Milliseconds 300 }
    Start-Sleep $wait
    $buf = New-Object byte[] 65536; $out = ''
    while ($s.DataAvailable) { $n = $s.Read($buf, 0, $buf.Length); $out += [Text.Encoding]::UTF8.GetString($buf, 0, $n) }
    $c.Close(); $out
}
# Карта грузится дольше, чем открывается порт: ждём, пока status покажет карту
for ($i = 0; $i -lt 30; $i++) {
    $st = Send-Netcon @('status') 2
    if ($st -match $Map -and $st -match 'players|bot') { break }
    Start-Sleep 5
}
'== status (хвост)'; ($st -split "`n" | Select-Object -Last 12) -join "`n"
'== jointeam 1'
$r = Send-Netcon @('jointeam 1', 'bot_quota') 3
($r -split "`n" | Where-Object { $_ -match 'bot_quota|team|spectat|Unknown' } | Select-Object -Last 6) -join "`n"
