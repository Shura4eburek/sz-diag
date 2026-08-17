$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Нагрузка на накопитель ЗАПИСЬЮ + обратным чтением со сверкой, в свободное место.
# Дополняет `disk-stress.ps1`, который умеет только читать.
#
# Грабля (СЗ 161346, бэклог п.133): в журнале клиента `disk 7` — это «bad block», а такие
# ошибки и reset контроллера (`stornvme 129`) чаще вылезают на ЗАПИСИ. Пять прогонов подряд
# гоняли только чтение (7,9 ТБ), дефект не воспроизвели — при том, что писать по диску,
# на который указывает журнал, не пробовали ни разу.
#
# ДАННЫЕ КЛИЕНТА НЕ ТРОГАЕМ: пишем только собственный файл в своей папке и удаляем его в
# конце. Существующие файлы не открываются на запись вообще.
$Drives      = 'C:'          # ← куда пишем. На 161346 цель — клиентский SNV3S500G (C:)
$Minutes     = 230
$FileGB      = 8             # размер рабочего файла на диск
$BlockMB     = 4             # блок записи: крупный, чтобы гнать очередь, а не IOPS
$MinFreeGB   = 40            # ниже этого порога свободного места не опускаемся ни при каких
# БЮДЖЕТ ЗАПИСИ. Грабля (161346): первый запуск без лимита выдал 2 ГБ/с и за 4 часа сжёг бы
# несколько ТБ ресурса записи на КЛИЕНТСКОМ диске (TBW у NV3 500G ~160 ТБ, то есть проценты
# ресурса за один прогон). Диагностическая ценность от терабайтов не растёт — нужны время
# под нагрузкой и переходы, а не объём. Исчерпав бюджет, тест продолжает держать нагрузку
# ЧТЕНИЕМ до конца отведённого времени.
$WriteCapGB  = 640
# Пауза между проходами делает две вещи. Первая: размазывает бюджет записи по всему прогону,
# иначе он выбирается за полчаса (161346: 512 ГБ за 35 минут на 2 ГБ/с) и дальше тест вхолостую
# читает. Вторая: даёт переходы «простой -> полная нагрузка» — на них и ловятся и слабое
# питание, и NVMe, который плохо выходит из энергосберегающего состояния (`stornvme 129`).
$PauseSec    = 180
$Log         = 'C:\ProgramData\szdiag\disk-write-stress.log'

New-Item -ItemType Directory -Path (Split-Path $Log) -Force -ErrorAction SilentlyContinue | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Log = $Log -replace '\.log$', "-$stamp.log"
$sw = [IO.StreamWriter]::new($Log, $false, [Text.UTF8Encoding]::new())
$sw.AutoFlush = $true

function Say([string]$m) {
    $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m
    $line
    if ($sw) { $sw.WriteLine($line) }
}

Say "СТАРТ записи. Диски: $($Drives -join ', '); файл $FileGB ГБ; блок $BlockMB МБ; лимит $Minutes мин"

# Случайные данные генерим один раз: цель — нагрузить накопитель, а не процессор.
$block = New-Object byte[] ($BlockMB * 1MB)
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($block)
$blockHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($block))

$deadline = (Get-Date).AddMinutes($Minutes)
$pass = 0
$totalWritten = 0L
$totalRead = 0L
$errors = 0

try {
    while ((Get-Date) -lt $deadline) {
        $pass++
        foreach ($drv in $Drives) {
            if ((Get-Date) -ge $deadline) { break }

            $free = (Get-PSDrive $drv.TrimEnd(':') -ErrorAction SilentlyContinue).Free
            if ($null -eq $free) { Say "  $drv — нет такого диска, пропуск"; continue }
            $freeGB = [math]::Round($free / 1GB, 1)
            if ($freeGB - $FileGB -lt $MinFreeGB) {
                Say "  $drv — свободно $freeGB ГБ, файл $FileGB ГБ упрётся в порог $MinFreeGB ГБ. ПРОПУСК"
                continue
            }

            $dir  = Join-Path $drv 'szdiag-write-test'
            New-Item -ItemType Directory -Path $dir -Force -ErrorAction SilentlyContinue | Out-Null
            $file = Join-Path $dir "w-$stamp.bin"
            $blocks = [int]($FileGB * 1024 / $BlockMB)

            $capReached = ($totalWritten / 1GB) -ge $WriteCapGB

            # --- запись (пока не выбран бюджет) ---
            $t0 = Get-Date
            if ($capReached) {
                if (-not (Test-Path $file)) {
                    # Бюджет выбран, а перечитывать нечего — держим нагрузку на том, что уже есть
                    Say "  бюджет записи $WriteCapGB ГБ выбран, рабочего файла нет — только чтение существующих данных"
                }
                else { Say "  бюджет записи выбран, проход $pass $drv — только чтение" }
            }
            else {
            try {
                # FileOptions::WriteThrough — мимо кэша ОС, иначе меряем оперативку, а не диск
                $fs = [IO.FileStream]::new($file, [IO.FileMode]::Create, [IO.FileAccess]::Write,
                                           [IO.FileShare]::None, $BlockMB * 1MB,
                                           [IO.FileOptions]::WriteThrough)
                for ($i = 0; $i -lt $blocks; $i++) {
                    $fs.Write($block, 0, $block.Length)
                    $totalWritten += $block.Length
                    if ((Get-Date) -ge $deadline) { break }
                }
                $fs.Flush($true)
                $fs.Dispose()
                $secs = ((Get-Date) - $t0).TotalSeconds
                $mbps = if ($secs -gt 0) { [math]::Round(($blocks * $BlockMB) / $secs) } else { 0 }
                Say ("  проход {0} {1} запись: {2} МБ за {3} с — {4} МБ/с (всего записано {5} ГБ из бюджета {6})" -f `
                    $pass, $drv, ($blocks * $BlockMB), [math]::Round($secs), $mbps,
                    [math]::Round($totalWritten / 1GB), $WriteCapGB)
            }
            catch {
                $errors++
                Say "  !!! ОШИБКА ЗАПИСИ на $drv (проход $pass): $($_.Exception.Message)"
                if ($fs) { try { $fs.Dispose() } catch {} }
                continue
            }
            }

            # --- обратное чтение со сверкой каждого блока ---
            # ЧИТАЕМ МИМО КЭША ОС (бэклог п.141): на 161346 сверка оказалась фиктивной —
            # файл 8 ГБ целиком лежал в кэше (32 ГБ ОЗУ), обратное чтение шло из оперативки,
            # `DataUnitsRead` по SMART не вырос вообще, и «расхождений 0» ничего не доказывало.
            # 0x20000000 = FILE_FLAG_NO_BUFFERING. Требует размеров, кратных сектору (4 МБ — ок),
            # но может упасть на выравнивании буфера — тогда честно откатываемся и пишем об этом.
            $t0 = Get-Date
            $bad = 0
            $noBuf = $true
            try {
                try {
                    $fs = [IO.FileStream]::new($file, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                                               [IO.FileShare]::None, $BlockMB * 1MB,
                                               ([IO.FileOptions]0x20000000 -bor [IO.FileOptions]::SequentialScan))
                }
                catch {
                    $noBuf = $false
                    Say "  ! чтение мимо кэша не поднялось ($($_.Exception.Message)) — читаю обычным путём, сверка НЕ доказательна"
                    $fs = [IO.FileStream]::new($file, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                                               [IO.FileShare]::None, $BlockMB * 1MB,
                                               [IO.FileOptions]::SequentialScan)
                }
                $buf = New-Object byte[] ($BlockMB * 1MB)
                $sha = [Security.Cryptography.SHA256]::Create()
                while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) {
                    $totalRead += $n
                    if ($n -eq $buf.Length) {
                        $h = [BitConverter]::ToString($sha.ComputeHash($buf))
                        # Расхождение = данные вернулись не те, что записали. Это не «медленно»,
                        # это молчаливое повреждение — самое ценное, что может дать тест.
                        if ($h -ne $blockHash) {
                            $bad++
                            $errors++
                            Say "  !!! РАСХОЖДЕНИЕ ДАННЫХ на $drv, смещение $($fs.Position - $n)"
                        }
                    }
                    if ((Get-Date) -ge $deadline) { break }
                }
                $fs.Dispose()
                $secs = ((Get-Date) - $t0).TotalSeconds
                $mbps = if ($secs -gt 0) { [math]::Round(($blocks * $BlockMB) / $secs) } else { 0 }
                $how = if ($noBuf) { 'мимо кэша' } else { 'ЧЕРЕЗ КЭШ — недоказательно' }
                Say "  проход $pass $drv чтение+сверка ($how): $mbps МБ/с, расхождений $bad"
            }
            catch {
                $errors++
                Say "  !!! ОШИБКА ЧТЕНИЯ на $drv (проход $pass): $($_.Exception.Message)"
                if ($fs) { try { $fs.Dispose() } catch {} }
            }

            # Смотрим журнал прямо по ходу: цель теста — не «скорость», а эти события.
            # Ждать разбора после прогона нельзя, машина может вырубиться и унести контекст.
            $watch = @(Get-WinEvent -FilterHashtable @{
                    LogName      = 'System'
                    ProviderName = @('disk', 'stornvme', 'storahci', 'Ntfs', 'volmgr',
                                     'Microsoft-Windows-WHEA-Logger')
                    StartTime    = (Get-Date).AddSeconds(-($PauseSec + 120))
                } -ErrorAction SilentlyContinue |
                Where-Object { $_.LevelDisplayName -notin 'Сведения', 'Information', 'Відомості' })
            foreach ($w in $watch) {
                $m = ($w.Message -replace '\s+', ' ').Trim()
                if ($m.Length -gt 120) { $m = $m.Substring(0, 120) }
                Say "  >>> СОБЫТИЕ ЖУРНАЛА: $($w.ProviderName) Id=$($w.Id) — $m"
            }

            # Файл НЕ удаляем между проходами: после исчерпания бюджета записи он остаётся
            # тем, что мы перечитываем, держа нагрузку без износа. Уборка — в finally.
            if ((Get-Date) -lt $deadline -and $PauseSec -gt 0) {
                Start-Sleep -Seconds $PauseSec
            }
        }
    }
}
finally {
    # Убираем за собой даже при обрыве: файл на 16 ГБ на клиентском диске оставлять нельзя
    foreach ($drv in $Drives) {
        $dir = Join-Path $drv 'szdiag-write-test'
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Say "ИТОГ: проходов $pass, записано $([math]::Round($totalWritten/1GB,1)) ГБ, прочитано $([math]::Round($totalRead/1GB,1)) ГБ, ошибок $errors"
    Say "Лог: $Log"
    if ($sw) { $sw.Dispose() }
}
