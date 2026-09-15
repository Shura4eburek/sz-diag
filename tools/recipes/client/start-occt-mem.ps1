$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# ЗАПУСК OCCT Memtest С ГАРАНТИРОВАННЫМ ОТЧЁТОМ (задача под SYSTEM, рук у машины не нужно).
#
# Грабли, которые его породили (СЗ 162367, 24-25.08.2026):
#  - 24.08 прогон честно отработал час (сенсоры: CPU 100 %, 145 Вт, Tmax 83,4 °C), а РЕЗУЛЬТАТА
#    не осталось нигде: ни в `C:\OCCT`, ни в `Documents\OCCT` (папки нет вообще). Причина —
#    OCCTCmd запускали без `--auto-save-report`: без этого ключа CLI не пишет отчёт в принципе,
#    сколько бы ни молотил. Час диагностики в мусор;
#  - `SaveReportPath` в `OCCT.config.json` = `C:\OCCT`, но сам по себе он ничего не сохраняет —
#    это только каталог по умолчанию для GUI;
#  - лицензия на клиенте лежала как `license (2).oke` (след второго скачивания) — OCCT такое имя
#    НЕ ВИДИТ и работает как без лицензии. Копируем в `license.oke` до старта;
#  - `--auto-close=true` обязателен: иначе OCCTCmd висит после расписания и следующий прогон
#    стартует поверх, а оба врут в лог.
#
# Приёмка делается ПО ПАМЯТИ, а не по «задача запущена»: сразу после старта свободной памяти
# должно остаться ~15 % от общей. Если свободно почти всё — тест не взял память и стоит.
#
#   szcli exec <СЗ> -f tools\recipes\client\start-occt-mem.ps1
#   (расписание готовит make-mem-schedule.ps1; итог снимает check-occt-result.ps1)

$Sz       = '162367'              # ← номер СЗ
$Schedule = 'schedule-mem.json'   # ← расписание (make-mem-schedule.ps1)
$Tag      = 'EXPO6000'            # ← метка конфигурации в имя отчёта: EXPO6000 / JEDEC4800
$Suffix   = 'mem'                 # ← в имя задачи: szdiag-occt<Suffix>-<СЗ>
# Лимит задачи должен быть ЗАВЕДОМО БОЛЬШЕ расписания (162003, 11.09): лимит ровно в
# длину прогона убивает OCCTCmd в тот момент, когда он пишет отчёт, — прогон отработал,
# результата нет. Держим час запаса.
$LimitHours = 4                   # ← лимит задачи, ч (расписание + запас)

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $proc) { 'агент не найден — не от чего считать путь к tools\occt'; return }
$occt = @(
    (Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'),
    (Join-Path $env:ProgramData 'szdiag\tools\occt')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $occt) { 'tools\occt нет — сначала szcli push <СЗ> occt'; return }

# Лицензия с «(2)» в имени = OCCT её не видит.
$good = Join-Path $occt 'license.oke'
if (-not (Test-Path $good)) {
    $bad = Get-ChildItem $occt -Filter '*.oke' | Select-Object -First 1
    if ($bad) { Copy-Item $bad.FullName $good -Force; "лицензия: скопировал $($bad.Name) -> license.oke" }
    else { 'ВНИМАНИЕ: .oke не найден — OCCT пойдёт без лицензии' }
}

# ЛИЦЕНЗИЯ: проверяем СРОК, а не наличие файла (162003, 15.09, бэклог п.272).
# Просроченная лицензия не роняет OCCT — он поднимает модальное окно «No valid license
# found» (в сессии 0 невидимое), при этом резервирует 85 % ОЗУ, поэтому приёмка «взял
# память» даёт ложное ДА. Так сожгли два прогона по 3+ часа: приборно 0.0 мин нагрузки
# за 233 мин. Дальше стартовать нельзя.
$okeHead = (Get-Content $good -Raw).Split('|')[0]
try {
    $okeTxt  = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($okeHead))
    $okeTill = [datetime]::ParseExact($okeTxt.Split(';')[2], 'yyyy/MM/dd', $null)
    $okeDays = ($okeTill - (Get-Date).Date).Days
    if ($okeDays -lt 0) {
        'ЛИЦЕНЗИЯ OCCT ПРОТУХЛА {0:dd.MM.yyyy} ({1} дн. назад) — прогон НЕ ЗАПУЩЕН: тест не пойдёт, а выглядеть будет как идущий' -f $okeTill, [math]::Abs($okeDays)
        return
    }
    'лицензия OCCT: до {0:dd.MM.yyyy} (осталось {1} дн.)' -f $okeTill, $okeDays
} catch { 'ВНИМАНИЕ: срок лицензии не разобрать — прогон может не пойти' }

$sched = Join-Path $occt $Schedule
if (-not (Test-Path $sched)) { "нет расписания $sched — сначала make-mem-schedule.ps1"; return }

# Имя бинаря НЕ зашиваем (162003, 15.09): в раздаче лежит `OCCTEnterprise.exe`, а рецепт
# звал `OCCTCmd.exe` — задача стартовала и падала с LastTaskResult=0x80070002 («файл не
# найден»), то есть трёхчасовой прогон «шёл» в пустоту. Проверка — occt-binary-probe.ps1.
$exe = @('OCCTCmd.exe', 'OCCTEnterprise.exe', 'OCCT.exe') |
    ForEach-Object { Join-Path $occt $_ } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $exe) { "в $occt нет ни OCCTCmd.exe, ни OCCTEnterprise.exe — раздача битая"; return }
"бинарь: $exe"

New-Item -ItemType Directory -Path 'C:\OCCT' -Force | Out-Null
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$report = "C:\OCCT\memtest-$Tag-$stamp.html"   # рядом лягут .json и .csv — json удобнее парсить
$task   = "szdiag-occt$Suffix-$Sz"

Get-Process OCCTCmd, OCCTEnterprise, OCCT -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue

$argline = 'test --schedule="{0}" --auto-start=true --auto-save-report=true --report-file="{1}" --overwrite-report-file=true --auto-close=true' -f $sched, $report
$action    = New-ScheduledTaskAction -Execute $exe -Argument $argline -WorkingDirectory $occt
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::FromHours($LimitHours))
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $task
"задача $task запущена, отчёт: $report"

Start-Sleep -Seconds 25
$pname = [IO.Path]::GetFileNameWithoutExtension($exe)
$p = Get-Process $pname -ErrorAction SilentlyContinue
if ($p) { '{0} жив: pid {1}, старт {2:HH:mm:ss}' -f $pname, $p.Id, $p.StartTime } else { 'ПРОЦЕСС НЕ ЗАПУСТИЛСЯ — смотри task-why.ps1' }
Get-ScheduledTaskInfo -TaskName $task | ForEach-Object { 'LastTaskResult=0x{0:X} (0x41301 = выполняется)' -f $_.LastTaskResult }
$os = Get-CimInstance Win32_OperatingSystem
'приёмка по памяти: свободно {0:N0} МБ из {1:N0} МБ' -f ($os.FreePhysicalMemory/1KB), ($os.TotalVisibleMemorySize/1KB)
