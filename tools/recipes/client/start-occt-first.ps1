$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Первый интерактивный прогон OCCT на машине, где задачи-донора ещё нет.
#
# Грабля (СЗ 161190): `start-occt.ps1` клонирует уже существующую `szdiag-occt-<СЗ>`, чтобы
# унаследовать принципал залогиненного пользователя — но на свежей машине её взять неоткуда,
# и рецепт честно падает «донор не найден». Создавать задачу через `schtasks /ru <user> /it`
# тоже не выходит: без пароля учётки она не заводится, а пароля клиента у нас нет и быть
# не должно. Рабочий путь — XML с `LogonType=InteractiveToken` и SID **реально залогиненного**
# пользователя: задача запускается в его сессии, 3D-сцена рендерится, пароль не нужен.
#
# 3D-тесты (Gpu3d/GpuUnreal/PowerSupply) в сессии 0 не работают — без живой сессии смысла
# запускать нет, скрипт это проверяет и останавливается.
#
#   szcli exec <СЗ> -f tools\recipes\client\start-occt-first.ps1
$Sz       = '161190'              # ← номер СЗ
$Schedule = 'schedule-gpu.json'   # ← какое расписание гнать
$Suffix   = 'gpu'                 # ← в имя задачи: szdiag-occt<Suffix>-<СЗ>

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$occt = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'
$sched = Join-Path $occt $Schedule
if (-not (Test-Path $sched)) { "нет расписания $sched — сначала make-gpu-schedule.ps1"; return }

# Залогиненного ищем по владельцу explorer.exe: Win32_LogonSession врёт про давно закрытые сессии.
$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
if (-not $expl) { 'НЕТ живой сессии (explorer не запущен) — 3D-тесты рендерить негде, нужен вход в Windows'; return }
$owner = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
$user = "$($owner.Domain)\$($owner.User)"
$sid = (New-Object Security.Principal.NTAccount($owner.Domain, $owner.User)).Translate([Security.Principal.SecurityIdentifier]).Value
"сессия: $user (SID $sid), explorer pid=$($expl.ProcessId)"

$task = "szdiag-occt$Suffix-$Sz"
$args = '/c start "OCCT ' + $Sz + ' ' + $Suffix + '" /min "' + $occt + '\OCCTCmd.exe" test --schedule="' + $sched + '" --auto-start'
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>szdiag OCCT $Suffix для СЗ $Sz</Description></RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <UserId>$sid</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
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
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>cmd.exe</Command><Arguments>$args</Arguments></Exec>
  </Actions>
</Task>
"@
$tmp = Join-Path $env:TEMP "$task.xml"
[IO.File]::WriteAllText($tmp, $xml, [Text.Encoding]::Unicode)

schtasks /delete /tn $task /f 2>$null | Out-Null
"create: " + (schtasks /create /tn $task /xml $tmp /f 2>&1)
"run:    " + (schtasks /run /tn $task 2>&1)
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

# 3D-сцена поднимается не мгновенно — приёмку делать check-load.ps1 через несколько минут,
# а не по факту «задача запущена».
Start-Sleep -Seconds 45
$p = Get-Process OCCTCmd, OCCT -ErrorAction SilentlyContinue
'процессы OCCT: ' + $(if ($p) { ($p | ForEach-Object { "$($_.ProcessName) pid=$($_.Id)" }) -join ', ' } else { 'НЕ ЗАПУСТИЛИСЬ' })
$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
if (Test-Path $smi) {
    'GPU сейчас: ' + ((& $smi --query-gpu=pstate,utilization.gpu,clocks.current.graphics,fan.speed,temperature.gpu --format=csv,noheader,nounits) -join '')
}
