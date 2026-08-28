$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# КОМПАКТНЫЙ тест записи + обратного чтения со сверкой на несколько томов сразу.
# Не замена disk-stress-write.ps1 (там богаче отчёт и защита), а его аварийный дубль:
# помещается в одну ssh-команду и потому заезжает на клиента, когда exec-канал агента лёг.
#
# Грабля (СЗ 161346, 26.08, бэклог п.215/216): после OCCT Combined exec-канал агента залип
# насмерть (heartbeat идёт, СЗ online, команды не разбираются, agent restart тоже не доехал).
# Единственным транспортом остался SSH, а полный рецепт на 15 КБ через него не заливался:
# sshd гонит команду через cmd.exe с лимитом 8191 символ, и чанки base64 по 4000 молча
# обрубались — на клиента доехало 482 байта из 15 482. Отсюда требование: скрипт должен быть
# компактным (~4 КБ), чтобы уехать целиком чанками по 2000.
#
# Запускать задачей под SYSTEM (под нагрузкой сессия ssh отваливается вместе с процессом):
#   schtasks /create /tn szdiag-dw-<СЗ> /tr "powershell -NoProfile -ExecutionPolicy Bypass
#            -File C:\ProgramData\szdiag\dw.ps1" /sc once /st 00:00 /ru SYSTEM /rl highest /f
#   schtasks /run /tn szdiag-dw-<СЗ>
# Результат — лог с флешем: C:\ProgramData\szdiag\disk-write-<дата>.log (переживает вырубон).
# На 161346 дал 21 проход, 168 ГБ на два M.2 одновременно, расхождений 0.
$ErrorActionPreference = 'Continue'
$Drives    = @('C:','D:')
$Minutes   = 45
$FileGB    = 4
$BlockMB   = 4
$MinFreeGB = 20
$WriteCapGB = 250
$PauseSec  = 120
$Log = 'C:\ProgramData\szdiag\disk-write-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log'
$sw = [IO.StreamWriter]::new($Log, $false, [Text.UTF8Encoding]::new())
$sw.AutoFlush = $true
function Say($m) { $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m; $sw.WriteLine($line) }

Say "СТАРТ. Диски: $($Drives -join ', '); файл $FileGB ГБ; блок $BlockMB МБ; лимит $Minutes мин; бюджет $WriteCapGB ГБ"
$deadline = (Get-Date).AddMinutes($Minutes)
$block = New-Object byte[] ($BlockMB * 1MB)
(New-Object Random 42).NextBytes($block)
$blocks = [int](($FileGB * 1GB) / ($BlockMB * 1MB))
$writtenGB = 0.0
$pass = 0
$errors = 0

while ((Get-Date) -lt $deadline) {
    $pass++
    foreach ($drv in $Drives) {
        if ((Get-Date) -ge $deadline) { break }
        $dir = Join-Path $drv 'szdiag-write-test'
        New-Item -ItemType Directory -Force $dir -ErrorAction SilentlyContinue | Out-Null
        $free = (Get-Volume -DriveLetter $drv[0]).SizeRemaining / 1GB
        if ($free -lt ($MinFreeGB + $FileGB)) { Say "$drv пропуск: свободно $([math]::Round($free,1)) ГБ"; continue }
        $file = Join-Path $dir 'w.bin'
        $mode = if ($writtenGB -ge $WriteCapGB) { 'read' } else { 'write' }
        try {
            if ($mode -eq 'write') {
                $t0 = Get-Date
                $fs = [IO.File]::Open($file, 'Create', 'Write', 'None')
                for ($i = 0; $i -lt $blocks; $i++) { $fs.Write($block, 0, $block.Length) }
                $fs.Flush($true); $fs.Close()
                $secW = ((Get-Date) - $t0).TotalSeconds
                $writtenGB += $FileGB
            }
            # обратное чтение со сверкой
            $t1 = Get-Date
            $bad = 0
            $buf = New-Object byte[] ($BlockMB * 1MB)
            $fs = [IO.File]::Open($file, 'Open', 'Read', 'Read')
            for ($i = 0; $i -lt $blocks; $i++) {
                $n = $fs.Read($buf, 0, $buf.Length)
                if ($n -ne $buf.Length) { $bad++; continue }
                for ($j = 0; $j -lt $buf.Length; $j += 65536) { if ($buf[$j] -ne $block[$j]) { $bad++; break } }
            }
            $fs.Close()
            $secR = ((Get-Date) - $t1).TotalSeconds
            $errors += $bad
            if ($mode -eq 'write') {
                Say ("{0} проход {1}: запись {2} ГБ за {3:N0} с ({4:N0} МБ/с), сверка {5:N0} с, расхождений {6}. Всего записано {7:N0} ГБ" -f $drv, $pass, $FileGB, $secW, ($FileGB*1024/$secW), $secR, $bad, $writtenGB)
            } else {
                Say ("{0} проход {1}: бюджет записи исчерпан, только чтение {2:N0} с, расхождений {3}" -f $drv, $pass, $secR, $bad)
            }
        } catch {
            $errors++
            Say ("{0} проход {1}: ОШИБКА — {2}" -f $drv, $pass, $_.Exception.Message)
        }
    }
    if ((Get-Date).AddSeconds($PauseSec) -lt $deadline) { Say "пауза $PauseSec с"; Start-Sleep -Seconds $PauseSec }
    else { break }
}

foreach ($drv in $Drives) {
    $dir = Join-Path $drv 'szdiag-write-test'
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $dir) { Say "$drv ВНИМАНИЕ: рабочая папка не удалилась" } else { Say "$drv рабочая папка убрана" }
}
Say "ФИНИШ. проходов $pass, записано $([math]::Round($writtenGB,0)) ГБ, расхождений/ошибок $errors"
$sw.Close()
