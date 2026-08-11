$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Реальная игровая нагрузка вместо синтетики: встроенный бенчмарк Cyberpunk 2077 в цикле.
#
# Грабля (СЗ 161346): четыре синтетических прогона подряд (Combined+Power, диски, CPU+Linpack,
# GPU-транзиенты + дисковый поток) дефект не воспроизвели, а у клиента 25 вырубонов за 4 дня —
# и все на реальных играх. OCCT не грузит связку «шейдеры + стриминг ассетов с NVMe + резкие
# смены сцены», а именно она и была рабочим режимом машины.
#
# Запускать ТОЛЬКО интерактивной задачей (schtasks /it): игра — GUI-процесс, из сессии агента
# он не поднимется. Лог пишется построчно с flush — переживает вырубон, а по нему видно,
# на какой итерации машина умерла.
$Game    = 'F:\Games\Steam\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe'
$Args    = '--launcher-skip', '-benchmark'
$Hours   = 3      # ← сколько всего гонять
$MaxRun  = 20     # ← минут на одну итерацию: бенчмарк короче, это защита от зависшего окна
$Log     = 'C:\ProgramData\szdiag\game-loop.log'

New-Item -ItemType Directory -Path (Split-Path $Log) -Force -ErrorAction SilentlyContinue | Out-Null
$Log = $Log -replace '\.log$', ("-" + (Get-Date -Format 'HHmmss') + ".log")
# UTF8Encoding($true) — с BOM: без него PS 5.1 читает лог как ANSI и кириллица приезжает мусором
$sw = [IO.StreamWriter]::new($Log, $true, [Text.UTF8Encoding]::new($true))
$sw.AutoFlush = $true
function Say { param($m) $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m; $sw.WriteLine($line); $line }

if (-not (Test-Path $Game)) { Say ("НЕТ игры: " + $Game); $sw.Close(); throw "нет $Game" }
Say ("СТАРТ: " + (Split-Path $Game -Leaf) + " " + ($Args -join ' ') + ", план $Hours ч")

$deadline = (Get-Date).AddHours($Hours)
$i = 0
while ((Get-Date) -lt $deadline) {
    $i++
    $t0 = Get-Date
    # ${i}: обязательны фигурные скобки — "$i:" PowerShell читает как scope-квалификатор и падает
    Say "итерация ${i}: запуск"
    $p = Start-Process -FilePath $Game -ArgumentList $Args -WorkingDirectory (Split-Path $Game -Parent) -PassThru
    $limit = $t0.AddMinutes($MaxRun)
    while (-not $p.HasExited -and (Get-Date) -lt $limit) { Start-Sleep -Seconds 10 }
    if (-not $p.HasExited) {
        Say ("итерация {0}: не вышла за {1} мин — закрываем" -f $i, $MaxRun)
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    $mins = [Math]::Round(((Get-Date) - $t0).TotalMinutes, 1)
    Say ("итерация {0}: завершена за {1} мин" -f $i, $mins)
    # игре нужно освободить видеопамять и закрыть свои процессы, иначе следующий запуск падает
    Start-Sleep -Seconds 30
}
Say "ФИНИШ: итераций $i, вырубонов за прогон не было (иначе этой строки не будет)"
$sw.Close()
