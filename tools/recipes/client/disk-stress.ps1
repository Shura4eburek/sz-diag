$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Нагрузка на накопитель ЧТЕНИЕМ реальных файлов + случайный доступ — имитация того, как
# игра тянет ассеты. Пишет прогресс построчно с flush, поэтому переживает вырубон.
#
# Грабля (СЗ 161346): OCCT 174 минуты на полной мощности (CPU 100 %, GPU 100 %, 301 Вт)
# не воспроизвёл дефект, потому что **накопитель он не трогает вообще**. А в журнале
# клиента вырубоны шли в связке с `disk 7` (bad block) и `stornvme 129` (reset контроллера).
# Игры грузятся с F: потоково — этого фактора в синтетике не было.
#
# ТОЛЬКО ЧТЕНИЕ: данные клиента не изменяются (в заявке прямой запрет на форматирование).
# Побочно это ещё и проверка читаемости поверхности: нечитаемый блок даст исключение с
# конкретным файлом и смещением.
$Drives      = 'C:', 'F:'   # ← какие диски мучаем (оба: версию про конкретный накопитель
                            #    нельзя проверить, читая только один — СЗ 161346)
$Minutes     = 180          # ← сколько гнать
$MinFileMB   = 64           # файлы мельче не берём: на них не разогнать очередь
$Log         = 'C:\ProgramData\szdiag\disk-stress.log'

# На системном диске часть файлов открыть нельзя в принципе (pagefile, реестр, файлы в
# работе). Это НЕ ошибка накопителя, и смешивать её с настоящими отказами нельзя —
# иначе тест "найдёт" дефект на любой живой машине.
$SkipPaths = '\pagefile.sys', '\hiberfil.sys', '\swapfile.sys', '\Windows\System32\config',
             '\Windows\Temp', '\$Recycle.Bin', '\System Volume Information'
$BusyMarks = 'used by another process', 'being used', 'Access to the path', 'denied',
             'Отказано в доступе', 'используется другим процессом'

New-Item -ItemType Directory -Path (Split-Path $Log) -Force -ErrorAction SilentlyContinue | Out-Null
# Имя с меткой времени: общий файл держит открытым предыдущий прогон (AutoFlush), и повторный
# запуск получает отказ. На 161346 это дало $sw = $null и скрипт молча ехал дальше, роняя
# каждую запись в лог — «ошибка» сыпалась в вывод, а прогресса не было видно вообще.
$Log = $Log -replace '\.log$', ("-" + (Get-Date -Format 'HHmmss') + ".log")
$sw = [IO.StreamWriter]::new($Log, $true, [Text.UTF8Encoding]::new())
if (-not $sw) { throw "не удалось открыть лог $Log — писать некуда, прогон бессмыслен" }
$sw.AutoFlush = $true    # без этого при вырубоне теряется ровно то, что нужно
function Say { param($m) $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m; $sw.WriteLine($line); $line }

Say ("СТАРТ: диски " + ($Drives -join ', ') + ", план $Minutes мин, только чтение")

$files = @()
foreach ($drv in $Drives) {
    $part = @(Get-ChildItem "$drv\" -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -gt ($MinFileMB * 1MB) } |
        Where-Object { $f = $_.FullName; -not ($SkipPaths | Where-Object { $f -like "*$_*" }) } |
        Select-Object -First 200)
    Say ("  $drv : отобрано файлов " + $part.Count + ", " + [math]::Round((($part | Measure-Object Length -Sum).Sum)/1GB,1) + " ГБ")
    $files += $part
}
if ($files.Count -eq 0) { Say "НЕТ подходящих файлов — нечем грузить"; $sw.Close(); return }
Say "файлов в наборе: $($files.Count), суммарно $([math]::Round((($files | Measure-Object Length -Sum).Sum)/1GB,1)) ГБ"

# Себя придушить обязательно: на 161346 тест без ограничений выжрал очередь ввода-вывода
# системного диска, машина перестала отвечать и связь с агентом отвалилась. Тест, который
# кладёт канал управления, не диагностирует — он мешает: вырубон от дефекта и «задавили
# сами» с хоста выглядят одинаково (СЗ уходит в offline).
try {
    $me = [Diagnostics.Process]::GetCurrentProcess()
    $me.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
} catch { }

$deadline = (Get-Date).AddMinutes($Minutes)
$buf      = New-Object byte[] (1MB)
$small    = New-Object byte[] (16KB)
$rnd      = [Random]::new()
$PauseMs  = 60    # пауза между файлами: даёт ОС и агенту дышать
$RandReads = 50   # случайных чтений на файл (было 200 — это и душило очередь)
$totalGB  = 0.0
$errors   = 0    # ошибки ввода-вывода = претензии к железу
$bugs     = 0    # ошибки самого скрипта — считаем отдельно, иначе они читаются как дефект
$busy     = 0    # «файл занят/нет доступа» — норма на системном диске, не отказ железа
$passes   = 0

while ((Get-Date) -lt $deadline) {
    $passes++
    foreach ($f in $files) {
        if ((Get-Date) -ge $deadline) { break }
        try {
            $fs = [IO.FileStream]::new($f.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                [IO.FileShare]::ReadWrite, 1MB, [IO.FileOptions]::SequentialScan)
            try {
                # Последовательное чтение — прогрев канала и проверка поверхности.
                while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
                    $totalGB += $n / 1GB
                    if ((Get-Date) -ge $deadline) { break }
                }
                # Случайные мелкие чтения — этим контроллер грузится сильнее, чем линейным потоком.
                # NextInt64 здесь нельзя: он есть только в .NET 6+, а PowerShell 5.1 сидит на
                # .NET Framework — вызов падает, и лог заполняется «ошибками чтения», которых нет.
                if ($fs.Length -gt 1MB) {
                    $max = $fs.Length - $small.Length
                    for ($i = 0; $i -lt $RandReads; $i++) {
                        $fs.Position = [long]($rnd.NextDouble() * $max)
                        $totalGB += $fs.Read($small, 0, $small.Length) / 1GB
                    }
                }
            } finally { $fs.Dispose() }
            Start-Sleep -Milliseconds $PauseMs
        } catch [System.IO.IOException] {
            # «Файл занят» на системном диске — норма, а не отказ железа. Считаем отдельно,
            # иначе тест «найдёт» дефект на любой работающей машине.
            $msg = $_.Exception.Message
            if ($BusyMarks | Where-Object { $msg -like "*$_*" }) {
                $script:busy++
            } else {
                $errors++
                Say "ОШИБКА ВВОДА-ВЫВОДА [$errors]: $($f.FullName) — $msg"
            }
        } catch [System.UnauthorizedAccessException] {
            $script:busy++
        } catch {
            # Всё остальное — дефект самого скрипта; мешать его с ошибками диска нельзя.
            $script:bugs++
            if ($script:bugs -le 3) { Say "ОШИБКА СКРИПТА (не диска): $($_.Exception.Message)" }
        }
    }
    Say ("проход {0} завершён: прочитано {1:N1} ГБ, ошибок {2}" -f $passes, $totalGB, $errors)
}

Say ("ФИНИШ: проходов {0}, прочитано {1:N1} ГБ, ошибок ввода-вывода {2}, пропущено занятых {3}, сбоев скрипта {4}" -f $passes, $totalGB, $errors, $busy, $bugs)
$sw.Close()
