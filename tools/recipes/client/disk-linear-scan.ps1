# Линейное последовательное чтение ВСЕГО накопителя по LBA (raw \.\PhysicalDriveN) —
# воспроизведение того, что клиент делает в Victoria/HD Tune: скан поверхности от начала
# до конца с замером скорости по зонам.
#
# Грабля (СЗ 161972): клиент заявил «в Victoria загрузка диска 100 %, скорость до 3 МБ/с,
# после чего компьютер вылетел». Наши прогоны (y-cruncher + OCCT, 3 часа) накопитель не
# трогают вообще, а `disk-stress.ps1` читает ФАЙЛЫ (случайный доступ, имитация игры) —
# это другой профиль нагрузки. Ни то ни другое не проверяет заявленный сценарий:
# непрерывное линейное чтение всего объёма. Отсюда отдельный рецепт.
#
# ТОЛЬКО ЧТЕНИЕ: raw-устройство открывается на чтение, ни один байт не пишется —
# данные клиента в безопасности (важно: на 161972 формат диска ещё не согласован).
#
# Читает мимо кэша ФС (raw device), поэтому меряет именно накопитель, а не память.
# Лог построчно с flush — переживает вырубон, а вырубон здесь и ожидается.

$DriveIndex  = 0      # какой физический диск (0 = системный NVMe)
$BlockMB     = 4      # блок чтения: крупный, чтобы гнать очередь как бенчмарк
$ReportGB    = 4      # как часто писать строку прогресса
$MaxMinutes  = 90     # потолок по времени: не успел — отчитается за покрытое
$SlowMBs     = 200    # ниже этого порога чанк считается ПРОСАДКОЙ и логируется отдельно
$Log         = 'C:\ProgramData\szdiag\disk-linear-scan.log'

New-Item -ItemType Directory -Path (Split-Path $Log) -Force -ErrorAction SilentlyContinue | Out-Null
$Log = $Log -replace '\.log$', ("-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")
$sw = [IO.StreamWriter]::new($Log, $false, [Text.UTF8Encoding]::new())
$sw.AutoFlush = $true   # без этого при вырубоне теряется ровно то, ради чего гнали
function Say { param($m) $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m; $sw.WriteLine($line); Write-Output $line }

$disk = Get-CimInstance Win32_DiskDrive -Filter "Index=$DriveIndex"
if (-not $disk) { Say "FATAL: PhysicalDrive$DriveIndex ne najden"; $sw.Close(); return }
$total = [int64]$disk.Size
Say ("START linear read: PhysicalDrive$DriveIndex " + $disk.Model.Trim() + ", " + [math]::Round($total/1GB,1) + " GB, block ${BlockMB}MB, limit ${MaxMinutes} min")
Say ("Log: $Log")

# Фоновая активность диска ДО старта: если она уже 100 %, «медленно» будет не про накопитель.
try {
    # Имена счётчиков локализованы (на русской винде англ. путь не резолвится) — берём через CIM.
    $pd = Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter "Name='_Total'" -EA Stop
    Say ("Do starta: disk busy " + (100 - $pd.PercentIdleTime) + " %, queue " + $pd.CurrentDiskQueueLength)
} catch { Say "Idle time: schetchik nedostupen" }

$path = '\\.\PhysicalDrive' + $DriveIndex
try {
    $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::None)
} catch { Say ("FATAL: ne otkryt $path : " + $_.Exception.Message); $sw.Close(); return }

$blockBytes = $BlockMB * 1MB
$buf        = New-Object byte[] $blockBytes
$reportEvery = [int64]($ReportGB * 1GB)

$read       = [int64]0        # сколько прочитано всего
$nextReport = $reportEvery
$chunkStart = [int64]0        # начало текущего отчётного окна
$slow       = @()             # просадки
$errors     = @()             # ошибки чтения
$speeds     = @()             # скорость по окнам, МБ/с
$deadline   = (Get-Date).AddMinutes($MaxMinutes)
$swTotal    = [Diagnostics.Stopwatch]::StartNew()
$swChunk    = [Diagnostics.Stopwatch]::StartNew()

while ($read -lt $total) {
    if ((Get-Date) -gt $deadline) { Say "STOP: ischerpan limit vremeni ${MaxMinutes} min"; break }
    try {
        $n = $fs.Read($buf, 0, $blockBytes)
    } catch {
        $lba = [math]::Round($read/1MB)
        $msg = "READ ERROR na smeschenii ${lba} MB: " + $_.Exception.Message
        Say $msg
        $errors += $msg
        if ($errors.Count -ge 20) { Say "STOP: 20 oshibok chteniya, dalshe smysla net"; break }
        # перешагиваем сбойный блок и продолжаем — карта дефектов важнее первой ошибки
        $read += $blockBytes
        try { $fs.Position = $read } catch { Say "STOP: seek posle oshibki ne udalsya"; break }
        continue
    }
    if ($n -le 0) { Say "Konec ustrojstva (Read vernul $n)"; break }
    $read += $n

    if ($read -ge $nextReport -or $read -ge $total) {
        $sec = $swChunk.Elapsed.TotalSeconds
        $mb  = ($read - $chunkStart) / 1MB
        $spd = if ($sec -gt 0) { $mb / $sec } else { 0 }
        $speeds += $spd
        $pct = [math]::Round(100.0 * $read / $total, 1)
        $line = "{0,6} GB / {1} GB  ({2,5} %)  {3,8} MB/s" -f [math]::Round($read/1GB,0), [math]::Round($total/1GB,0), $pct, [math]::Round($spd,1)
        if ($spd -lt $SlowMBs) {
            $line += "   <-- PROSADKA"
            $slow += [pscustomobject]@{ AtGB = [math]::Round($read/1GB,1); MBs = [math]::Round($spd,1); Time = (Get-Date -Format 'HH:mm:ss') }
        }
        Say $line
        $chunkStart = $read
        $nextReport = $read + $reportEvery
        $swChunk.Restart()
    }
}
$fs.Close()
$swTotal.Stop()

$elapsed = $swTotal.Elapsed
$avg = if ($elapsed.TotalSeconds -gt 0) { ($read/1MB) / $elapsed.TotalSeconds } else { 0 }
Say "----------------------------------------------------------"
Say ("ITOG: prochitano " + [math]::Round($read/1GB,1) + " GB iz " + [math]::Round($total/1GB,1) + " GB za " + $elapsed.ToString('hh\:mm\:ss'))
Say ("Srednyaya skorost: " + [math]::Round($avg,1) + " MB/s")
if ($speeds.Count -gt 0) {
    $st = $speeds | Measure-Object -Minimum -Maximum -Average
    Say ("Po oknam po ${ReportGB} GB: min " + [math]::Round($st.Minimum,1) + " / avg " + [math]::Round($st.Average,1) + " / max " + [math]::Round($st.Maximum,1) + " MB/s, okon " + $speeds.Count)
}
Say ("Prosadok nizhe ${SlowMBs} MB/s: " + $slow.Count)
foreach ($s in ($slow | Select-Object -First 40)) { Say ("   na " + $s.AtGB + " GB: " + $s.MBs + " MB/s v " + $s.Time) }
Say ("Oshibok chteniya: " + $errors.Count)
Say "DONE"
$sw.Close()
