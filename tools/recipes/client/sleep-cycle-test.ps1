$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# ЦИКЛ «сон -> RTC-пробуждение» как тест дефекта. Породила СЗ 161498: машина вимикається сама,
# при этом 65 минут OCCT (CPU-only -> CPU+данные -> CPU+GPU пик) выстояла без сбоев, а дома
# падала ночью в 02:49-03:33 и вечером в простое. Нагрузочные тесты такой дефект НЕ ловят.
#
# Признак дефекта — не «не проснулась» (это бывает от кривого wake), а ГРЯЗНОЕ выключение:
# событие 6008 на следующей загрузке. Лог пишется через AppendAllText (сразу на диск), иначе
# последняя строка перед вырубоном теряется в буфере — ровно то, ради чего тест и затевался.
#
# Это ИНСТАЛЛЯТОР: `szcli exec` гоняет скрипт инлайн ($MyInvocation.MyCommand.Path пустой),
# поэтому цикл не может скопировать сам себя — он лежит здесь как payload и кладётся в
# C:\OCCT\sleep-cycle.ps1, откуда его дальше дёргает самоперевзводящаяся задача под SYSTEM.
# Под SYSTEM, а не из сессии агента: процесс сессии умирает вместе с ней (грабля lhmmon).
#
# ВАЖНО (161498): StartWhenAvailable=false обязателен. С true просроченная задача срабатывает
# при СЛЕДУЮЩЕМ включении машины: если она легла во сне и её включили через сутки, то через
# 90 секунд она снова уснёт, а человек у корпуса не поймёт почему. Задача цикла обязана
# умирать вместе с пропущенным окном, а не воскресать.
# Стоп: szcli exec <СЗ> -f tools\recipes\client\sleep-cycle-stop.ps1
#   szcli exec <СЗ> -f tools\recipes\client\sleep-cycle-test.ps1
if (-not (Test-Path 'C:\OCCT')) { New-Item -ItemType Directory 'C:\OCCT' | Out-Null }
Remove-Item 'C:\OCCT\stop-sleep-test' -Force -ErrorAction SilentlyContinue

# ПРЕДОХРАНИТЕЛЬ (161498, 26.08): забытый цикл живёт вечно и делает машину недоступной —
# окно бодрствования 90 с, за него не успевает доехать ни szcli exec, ни стоп-рецепт.
# На 161498 цикл от 24.08 воскрес 26.08 при включении машины и снова уложил её спать;
# снимать пришлось офлайн из WinPE. Дедлайн пишем файлом: payload сверяется с ним
# на каждом WAKE и по истечении сам снимает задачу.
$MaxHours = 8
[IO.File]::WriteAllText('C:\OCCT\sleep-cycle-deadline', (Get-Date).AddHours($MaxHours).ToString('o'))
"предохранитель: цикл сам умрёт после {0:dd.MM HH:mm:ss}" -f (Get-Date).AddHours($MaxHours)

$payload = @'
$Sz            = '161498'
$SleepMinutes  = 4
$AwakeSeconds  = 90

$log  = 'C:\OCCT\sleep-test.log'
$stop = 'C:\OCCT\stop-sleep-test'
$self = 'C:\OCCT\sleep-cycle.ps1'
$task = "szdiag-sleepcycle-$Sz"

function Say($m) {
    $line = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $m
    [IO.File]::AppendAllText($log, $line + "`r`n", [Text.Encoding]::UTF8)
    $line
}

$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
Say ("=== WAKE === загрузка ОС: {0:dd.MM HH:mm:ss}, аптайм {1:hh\:mm\:ss}" -f $boot, ((Get-Date) - $boot))
$dirty = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 6008 } -MaxEvents 1 -ErrorAction SilentlyContinue
if ($dirty) {
    Say ("    последнее ГРЯЗНОЕ выключение: {0} {1}" -f $dirty.Properties[1].Value, $dirty.Properties[0].Value)
}
$w = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = @(42, 107) } -MaxEvents 4 -ErrorAction SilentlyContinue
if ($w) { Say ('    события сна: ' + (($w | Sort-Object TimeCreated | ForEach-Object { '{0:HH:mm:ss}/Id{1}' -f $_.TimeCreated, $_.Id }) -join ' ')) }
Say ('    lastwake: ' + ((powercfg /lastwake) -join ' ').Trim())

if (Test-Path $stop) { Say 'СТОП-файл на месте — цикл окончен'; schtasks /delete /tn $task /f 2>$null | Out-Null; return }

$dl = 'C:\OCCT\sleep-cycle-deadline'
$expired = $true
if (Test-Path $dl) {
    try { $expired = ([datetime]::Parse((Get-Content $dl -Raw).Trim()) -lt (Get-Date)) } catch { $expired = $true }
}
if ($expired) {
    Say 'ПРЕДОХРАНИТЕЛЬ: срок цикла истёк (или файл дедлайна потерян) — снимаем задачу и выходим'
    schtasks /delete /tn $task /f 2>$null | Out-Null
    return
}

Say "    ждём $AwakeSeconds с (агент реконнектится к hub)"
Start-Sleep -Seconds $AwakeSeconds
if (Test-Path $stop) { Say 'СТОП-файл появился — выходим'; schtasks /delete /tn $task /f 2>$null | Out-Null; return }

$at = (Get-Date).AddMinutes($SleepMinutes)
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>szdiag: цикл сна для СЗ $Sz</Description></RegistrationInfo>
  <Triggers><TimeTrigger><StartBoundary>$($at.ToString('yyyy-MM-ddTHH:mm:ss'))</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>true</WakeToRun>
    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-NoProfile -ExecutionPolicy Bypass -File "C:\OCCT\sleep-cycle.ps1"</Arguments>
    </Exec>
  </Actions>
</Task>
"@
$tmp = Join-Path $env:TEMP "$task.xml"
[IO.File]::WriteAllText($tmp, $xml, [Text.Encoding]::Unicode)
schtasks /delete /tn $task /f 2>$null | Out-Null
$create = schtasks /create /tn $task /xml $tmp /f 2>&1
Say ('    задача пробуждения на {0:HH:mm:ss}: {1}' -f $at, ($create -join ' '))
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

$wt = (powercfg /waketimers) -join ' '
Say ('    waketimers: ' + $wt.Trim())
if ($wt -match 'no active wake timers') { Say '!!! таймер НЕ взведён — сон отменяем, иначе машина не проснётся'; return }

Say '=== SLEEP === уходим в сон'
rundll32.exe powrprof.dll,SetSuspendState 0,1,0
'@
[IO.File]::WriteAllText('C:\OCCT\sleep-cycle.ps1', $payload, (New-Object Text.UTF8Encoding $true))
"payload записан: {0:N1} КБ" -f ((Get-Item 'C:\OCCT\sleep-cycle.ps1').Length / 1KB)

# Синтаксическую годность payload проверяем ДО первого сна: ошибка внутри задачи под SYSTEM
# никуда не всплывёт — машина просто уснёт и не проснётся (память: PSParser перед отправкой).
$err = $null
[void][Management.Automation.PSParser]::Tokenize((Get-Content 'C:\OCCT\sleep-cycle.ps1' -Raw), [ref]$err)
if ($err) { throw ("payload не парсится: " + ($err | ForEach-Object { $_.Message } | Select-Object -First 3)) }
'payload: синтаксис ок'

'--- стартуем первый цикл ---'
& 'C:\OCCT\sleep-cycle.ps1'
