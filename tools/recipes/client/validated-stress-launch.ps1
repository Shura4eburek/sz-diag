$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Шаблон валидированного запуска самописной (не OCCT/TM5) синтетической нагрузки.
#
# Родился из СЗ 160587 (2026-08-04, бэклог п.45, вторая половина): нагрузчик переписали
# "пожирнее" (Vector<double> из System.Numerics) и запустили 12 процессов на 45 минут.
# Лог мониторинга все 90 итераций показывал "живых нагрузчиков=0, загрузка CPU=0-10%":
# PowerShell 5.1 сидит на .NET Framework, где System.Numerics.Vectors НЕ подгружается без
# явного -ReferencedAssemblies - Add-Type падал, дочерний процесс выходил мгновенно.
# Start-Process этого не показывает никак: stderr дочернего никуда не шёл, и снаружи прогон
# выглядел "идущим". Итог - 45 минут простоя вместо стресса и снова ложный "отрицательный"
# результат (тот же класс ошибки, что и с окном в 3 минуты на ядро - см. WindowCalculator
# в szcli reboots --plan).
#
# Обязательные шаги любого самописного стресс-прогона:
#   1. -RedirectStandardError во временный файл на каждый дочерний процесс - иначе ошибка
#      компиляции/запуска теряется полностью;
#   2. через 20-30 с проверить HasExited и LoadPercentage - если живых < половины или
#      CPU < 60%, ПРЕРВАТЬ прогон с явной ошибкой (и содержимым stderr), а не досиживать
#      до конца с нулевым результатом;
#   3. в нагрузчике под PS 5.1 не использовать типы вне базового .NET Framework
#      (System.Numerics.Vector<T>, System.Memory, Span<T>) без -ReferencedAssemblies;
#   4. если нужен просто максимум транзиентов CPU+GPU - штатный OCCT/TM5 надёжнее
#      самописной синтетики: у него виден собственный лог, и он не умирает молча.
#
# Использование: подставь свой $WorkerScript (путь к .ps1 воркера на клиенте) и число
# процессов, дальше рецепт сам валидирует старт и либо продолжает, либо прерывается явно.
#   szcli exec <СЗ> -f tools\recipes\client\validated-stress-launch.ps1

$WorkerScript = 'C:\ProgramData\szdiag\stress-worker.ps1'   # свой скрипт нагрузки на ядро/поток
$WorkerCount = 12
$ValidateAfterSeconds = 25
$MinAliveFraction = 0.5
$MinCpuLoadPercent = 60

if (-not (Test-Path $WorkerScript)) {
    "воркер не найден: $WorkerScript - положи скрипт нагрузки на клиент перед запуском рецепта"
    return
}

$procs = @()
for ($i = 0; $i -lt $WorkerCount; $i++) {
    $errFile = Join-Path $env:TEMP "szdiag-stress-err-$i.log"
    if (Test-Path $errFile) { Remove-Item $errFile -Force -ErrorAction SilentlyContinue }
    $p = Start-Process powershell.exe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $WorkerScript) `
        -RedirectStandardError $errFile -PassThru -WindowStyle Hidden
    $procs += [PSCustomObject]@{ Process = $p; ErrFile = $errFile }
}
"запущено воркеров: $($procs.Count)"

Start-Sleep -Seconds $ValidateAfterSeconds

$alive = 0
$aliveItems = @()
foreach ($item in $procs) {
    $item.Process.Refresh()
    if (-not $item.Process.HasExited) { $alive++; $aliveItems += $item }
}
$cpu = (Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Measure-Object -Property LoadPercentage -Average).Average
$cpuSource = 'Win32_Processor.LoadPercentage'
# I-13 (ревью волны 2): Win32_Processor.LoadPercentage — тот же мёртвый на части клиентских
# машин счётчик, что описан в #151 (process-io-top.ps1). Measure-Object -Average по null-у
# отдаёт 0, а не $null, и "0 -lt 60"/"$null -lt 60" в PowerShell тоже true — двенадцать честно
# жгущих CPU воркеров получали "нагрузка не пошла" от сломанного датчика, а не от реального
# простоя. Если счётчик молчит (null/0) при живых воркерах — меряем реальное CPU-время самих
# процессов за короткое окно вместо того, чтобы доверять единственному источнику.
if ($alive -gt 0 -and (($null -eq $cpu) -or ($cpu -eq 0))) {
    $before = @{}
    foreach ($item in $aliveItems) { $item.Process.Refresh(); $before[$item.Process.Id] = $item.Process.TotalProcessorTime }
    $probeSec = 2
    Start-Sleep -Seconds $probeSec
    $consumed = [TimeSpan]::Zero
    foreach ($item in $aliveItems) {
        try { $item.Process.Refresh() } catch { continue }
        if ($item.Process.HasExited -or -not $before.ContainsKey($item.Process.Id)) { continue }
        $delta = $item.Process.TotalProcessorTime - $before[$item.Process.Id]
        if ($delta.Ticks -gt 0) { $consumed += $delta }
    }
    $capacitySec = $probeSec * $aliveItems.Count
    if ($capacitySec -gt 0) {
        $cpu = [math]::Round(100.0 * $consumed.TotalSeconds / $capacitySec, 1)
        $cpuSource = "суммарное CPU-время $($aliveItems.Count) живых процессов за $probeSec c (счётчик LoadPercentage молчал)"
    }
}
"через $ValidateAfterSeconds c: живых воркеров=$alive/$($procs.Count), загрузка CPU=$cpu% ($cpuSource)"

$requiredAlive = [math]::Ceiling($procs.Count * $MinAliveFraction)
if ($alive -lt $requiredAlive -or $cpu -lt $MinCpuLoadPercent) {
    "НАГРУЗКА НЕ ПОШЛА: живых $alive из $($procs.Count) (нужно >= $requiredAlive), CPU $cpu% (нужно >= $MinCpuLoadPercent%)"
    foreach ($item in $procs) {
        if (Test-Path $item.ErrFile) {
            $err = Get-Content $item.ErrFile -Raw -ErrorAction SilentlyContinue
            if ($err) { "--- stderr $($item.Process.Id) ---"; $err }
        }
        if (-not $item.Process.HasExited) { try { $item.Process.Kill() } catch {} }
    }
    throw "нагрузка не пошла - прогон прерван на $ValidateAfterSeconds секунде вместо часового ожидания вслепую"
}

"нагрузка подтверждена - можно продолжать штатный мониторинг (szcli sensors start / test run)"
