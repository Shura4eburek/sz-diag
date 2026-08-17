$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# GPU-стресс FurMark 2.x задачей в сессии залогиненного пользователя.
#
# Зачем, если есть OCCT: на 161190 раздаваемый `szcli push occt` оказался с **просроченной
# лицензией** (`license.oke` истёк 13.08, прогон 17.08) — OCCT молча не стартует, задача при
# этом рапортует `Last Result: 0` (бэклог п.169). FurMark бесплатный и на месте, а для вопроса
# «держит ли машина пиковую нагрузку по питанию» его furmark-демо не хуже: это классический
# power virus.
#
# Как и OCCT, 3D рендерится только в живой сессии — под SYSTEM в сессии 0 смысла нет.
# Задача создаётся с `LogonType=InteractiveToken` и SID реально залогиненного пользователя,
# пароль учётки не нужен (её у нас и не должно быть).
#
#   szcli exec <СЗ> -f tools\recipes\client\start-furmark.ps1
$Sz      = '161190'          # ← номер СЗ
$Demo    = 'furmark-gl'      # ← furmark-gl | furmark-vk | furmark-knot-gl | furmark-knot-vk
$MaxTime = 2400              # ← секунд

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$fm = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\furmark\furmark.exe'
if (-not (Test-Path $fm)) { "нет $fm — сначала szcli push $Sz furmark"; return }

$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
if (-not $expl) { 'НЕТ живой сессии — 3D рендерить негде, нужен вход в Windows'; return }
$owner = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
$sid = (New-Object Security.Principal.NTAccount($owner.Domain, $owner.User)).Translate([Security.Principal.SecurityIdentifier]).Value
"сессия: $($owner.Domain)\$($owner.User) (SID $sid)"

$task = "szdiag-furmark-$Sz"
# --disable-demo-options убирает интерактивную панель, --no-score-box — окно результата в конце
# (иначе после прогона висит модалка и следующий запуск не стартует).
$a = "--demo $Demo --width 1920 --height 1080 --max-time $MaxTime --no-score-box --disable-demo-options"
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>szdiag FurMark для СЗ $Sz</Description></RegistrationInfo>
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
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$fm</Command>
      <Arguments>$a</Arguments>
      <WorkingDirectory>$(Split-Path $fm -Parent)</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@
$tmp = Join-Path $env:TEMP "$task.xml"
[IO.File]::WriteAllText($tmp, $xml, [Text.Encoding]::Unicode)
schtasks /delete /tn $task /f 2>$null | Out-Null
"create: " + (schtasks /create /tn $task /xml $tmp /f 2>&1)
"run:    " + (schtasks /run /tn $task 2>&1)
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

# Приёмка по живому процессу и по реальной загрузке GPU: «задача запущена» ничего не значит.
Start-Sleep -Seconds 40
$p = Get-Process furmark -ErrorAction SilentlyContinue
'процесс furmark: ' + $(if ($p) { "жив pid=$($p.Id)" } else { 'НЕ ЗАПУСТИЛСЯ' })
$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
if (Test-Path $smi) {
    for ($i = 0; $i -lt 4; $i++) {
        '   GPU: ' + ((& $smi --query-gpu=pstate,utilization.gpu,clocks.current.graphics,fan.speed,temperature.gpu --format=csv,noheader,nounits) -join '')
        Start-Sleep -Seconds 10
    }
}
