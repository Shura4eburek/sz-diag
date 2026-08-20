$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# TM5 (профиль anta777 Heavy5opt) интерактивной задачей ОТ АДМИНИСТРАТОРА.
#
# Три грабли, каждая делает прогон бессмысленным молча:
#   1) TM5 без прав админа уходит в "compatibility mode" и режет покрытие — в логе строка
#      "need to run with Administrator privileges!" (СЗ 161288, 31.07: помехи нашлись всё
#      равно, но прогон был неполный). Отсюда RunLevel=HighestAvailable.
#   2) `szcli push tm5` доставляет НАШ `bin\Cfg.link`, а в нём UNC `\mamoru\Share\...`,
#      которого с клиента не видно → TM5 берёт `bin\MT.cfg` = профиль **Default** (6 тестов)
#      вместо Heavy5opt (16 тестов, Time 500%). Рецепт переписывает Cfg.link на локальный путь.
#   3) TM5 — GUI, под SYSTEM в сессии 0 окна нет; задача заводится с InteractiveToken и SID
#      залогиненного юзера (пароль учётки не нужен), как в start-occt-first.ps1.
#   4) ГЛАВНОЕ (161288, бэклог п.182): запущенный так TM5 вываливает ~15 модальных алертов
#      «нужны права администратора» — по одному на рабочий процесс. Пока их не закроют РУКАМИ
#      на машине, тест не начинается: процессы висят с CPU-временем 0 и памятью 100 МБ вместо
#      почти всей ОЗУ. Удалённо окна не закрыть. Нет человека у машины — бери безоконные тесты
#      (start-ycruncher.ps1 или OCCT Memtest), а не TM5.
# Старый Log.txt отъезжает в Log-prev-<время>.txt, иначе приёмка читает чужие ошибки.
#
#   szcli exec <СЗ> -f tools\recipes\client\start-tm5.ps1
$Sz = '000000'   # <- номер СЗ

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$tm5  = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\tm5'
$exe  = Join-Path $tm5 'TM5.exe'
if (-not (Test-Path $exe)) { "нет $exe - сначала szcli push $Sz tm5"; return }

# --- профиль: Cfg.link должен указывать на локальный MT.cfg ---
$cfg = Join-Path $tm5 'MT.cfg'
$name = (Select-String -Path $cfg -Pattern 'Config Name=(.+)' | Select-Object -First 1).Matches.Groups[1].Value
[IO.File]::WriteAllText((Join-Path $tm5 'bin\Cfg.link'), $cfg, [Text.Encoding]::ASCII)
"профиль: $name  ($cfg)"

# --- старый лог в сторону ---
$log = Join-Path $tm5 'Log.txt'
if (Test-Path $log) {
    $bak = Join-Path $tm5 ("Log-prev-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Move-Item $log $bak -Force
    "старый Log.txt -> $(Split-Path $bak -Leaf)"
}

# --- сессия ---
$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
if (-not $expl) { 'НЕТ живой сессии (explorer не запущен) - TM5 GUI запускать негде'; return }
$owner = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
$sid = (New-Object Security.Principal.NTAccount($owner.Domain, $owner.User)).Translate([Security.Principal.SecurityIdentifier]).Value
"сессия: $($owner.Domain)\$($owner.User) (SID $sid)"

$task = "szdiag-tm5-$Sz"
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>szdiag TM5 для СЗ $Sz</Description></RegistrationInfo>
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
    <Exec><Command>$exe</Command><WorkingDirectory>$tm5</WorkingDirectory></Exec>
  </Actions>
</Task>
"@
$tmp = Join-Path $env:TEMP "$task.xml"
[IO.File]::WriteAllText($tmp, $xml, [Text.Encoding]::Unicode)
schtasks /delete /tn $task /f 2>$null | Out-Null
"create: " + (schtasks /create /tn $task /xml $tmp /f 2>&1)
"run:    " + (schtasks /run /tn $task 2>&1)
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

# Приёмка: сам процесс + первые строки лога. "compatibility mode" в логе = прав не хватило.
Start-Sleep -Seconds 40
# TM5 держит по процессу на поток: $p - массив, отсюда сумма памяти, а не $p.WorkingSet64.
$p = @(Get-Process TM5 -ErrorAction SilentlyContinue)
$ws = [int](($p | Measure-Object WorkingSet64 -Sum).Sum/1MB)
$cpu = [int](($p | Measure-Object CPU -Sum).Sum)
'процесс TM5: ' + $(if ($p.Count) { "живы {0} шт (pid {1}), суммарно {2} МБ, CPU-время {3} с" -f $p.Count, ($p.Id -join ','), $ws, $cpu } else { 'НЕ ЗАПУСТИЛСЯ' })
# Признак залипания на алертах: процессы есть, а работы нет. Heavy5opt за 40 с обязан
# забрать гигабайты и намотать CPU-время; 100 МБ и 0 с = ждёт, пока закроют окна.
if ($p.Count -and $ws -lt 1024 -and $cpu -lt 5) {
    '⚠ ТЕСТ НЕ ИДЁТ: похоже, TM5 ждёт закрытия ~15 алертов «нужны права администратора».'
    '  Закрыть окна можно только РУКАМИ на машине. Никого рядом нет — гони start-ycruncher.ps1'
    '  или OCCT Memtest (бэклог п.182).'
}
if (Test-Path $log) { '--- Log.txt ---'; Get-Content $log -Tail 10 }
