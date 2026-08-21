# Карта скорости чтения ПО ВСЕМУ объёму накопителя точками (sampling) + прицельный
# прогон по зоне. Это то, что рисует график в Victoria/HD Tune, только без GUI.
#
# Грабля (СЗ 161972, 21.08): сплошное линейное чтение (`disk-linear-scan.ps1`) нашло
# зону, где диск валится с 3600 МБ/с до 9,6 МБ/с — и в ней же намертво застряло:
# при такой скорости полный проход 1863 ГБ занял бы больше суток. Карту дефектных зон
# надо снимать точками (весь диск за минуты), а сплошное чтение оставлять для одной
# подозрительной зоны. Клиент ровно это и видел в Victoria: «загрузка 100 %, до 3 МБ/с».
#
# ТОЛЬКО ЧТЕНИЕ: raw-устройство открывается на чтение, ни байта не пишется.
$DriveIndex  = 0
$Mode        = 'map'    # 'map' — точки по всему диску; 'zone' — сплошняком по зоне
$Points      = 400      # для 'map': сколько точек
$SampleMB    = 32       # для 'map': сколько читать в каждой точке
$ZoneStartGB = 440      # для 'zone': границы прицельного прогона
$ZoneEndGB   = 520
$ZoneStepMB  = 256      # для 'zone': шаг отчёта
$MaxMinutes  = 25       # общий потолок
$SlowMBs     = 200      # порог просадки
$Log         = 'C:\ProgramData\szdiag\disk-zone-map.log'

New-Item -ItemType Directory -Path (Split-Path $Log) -Force -ErrorAction SilentlyContinue | Out-Null
$Log = $Log -replace '\.log$', ("-$Mode-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")
$sw = [IO.StreamWriter]::new($Log, $false, [Text.UTF8Encoding]::new())
$sw.AutoFlush = $true
function Say { param($m) $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m; $sw.WriteLine($line); Write-Output $line }
function Note { param($m) $sw.WriteLine(("{0:HH:mm:ss}  {1}" -f (Get-Date), $m)) }   # только в лог

$disk = Get-CimInstance Win32_DiskDrive -Filter "Index=$DriveIndex"
if (-not $disk) { Say "FATAL: PhysicalDrive$DriveIndex ne najden"; $sw.Close(); return }
$total = [int64]$disk.Size
$path = '\\.\PhysicalDrive' + $DriveIndex
try {
    $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::None)
} catch { Say ("FATAL: ne otkryt $path : " + $_.Exception.Message); $sw.Close(); return }

$deadline = (Get-Date).AddMinutes($MaxMinutes)
$slow = @(); $results = @()

if ($Mode -eq 'map') {
    Say ("MAP: " + $disk.Model.Trim() + ", " + [math]::Round($total/1GB,1) + " GB, tochek $Points po ${SampleMB} MB")
    $buf = New-Object byte[] ($SampleMB * 1MB)
    $step = [int64]($total / $Points)
    $step = [int64]([math]::Floor($step / 1MB) * 1MB)     # выравниваем на 1 МБ
    for ($i = 0; $i -lt $Points; $i++) {
        if ((Get-Date) -gt $deadline) { Say "STOP: limit ${MaxMinutes} min, sdelano $i tochek iz $Points"; break }
        $off = [int64]$i * $step
        if ($off + $buf.Length -gt $total) { break }
        try { $fs.Position = $off } catch { Say ("SEEK ERROR na " + [math]::Round($off/1GB,1) + " GB"); continue }
        $t = [Diagnostics.Stopwatch]::StartNew()
        try { $n = $fs.Read($buf, 0, $buf.Length) } catch {
            Say ("READ ERROR na " + [math]::Round($off/1GB,1) + " GB: " + $_.Exception.Message); continue
        }
        $t.Stop()
        $spd = if ($t.Elapsed.TotalSeconds -gt 0) { ($n/1MB) / $t.Elapsed.TotalSeconds } else { 0 }
        $atGB = [math]::Round($off/1GB,1)
        $results += [pscustomobject]@{ AtGB = $atGB; MBs = [math]::Round($spd,1) }
        Note ("  {0,8} GB  {1,8} MB/s" -f $atGB, [math]::Round($spd,1))
        if ($spd -lt $SlowMBs) {
            $slow += [pscustomobject]@{ AtGB = $atGB; MBs = [math]::Round($spd,1) }
            Say ("  PROSADKA: {0} GB  {1} MB/s" -f $atGB, [math]::Round($spd,1))
        }
        if ($i -gt 0 -and $i % 50 -eq 0) { Say ("  ... $i / $Points tochek, prosadok " + $slow.Count) }
    }
} else {
    Say ("ZONE: sploshnoe chtenie $ZoneStartGB - $ZoneEndGB GB, shag otcheta ${ZoneStepMB} MB")
    $blk = 4MB
    $buf = New-Object byte[] $blk
    $pos = [int64]$ZoneStartGB * 1GB
    $end = [int64]$ZoneEndGB * 1GB
    if ($end -gt $total) { $end = $total }
    $fs.Position = $pos
    $winStart = $pos
    $winSw = [Diagnostics.Stopwatch]::StartNew()
    while ($pos -lt $end) {
        if ((Get-Date) -gt $deadline) { Say ("STOP: limit ${MaxMinutes} min na " + [math]::Round($pos/1GB,2) + " GB"); break }
        try { $n = $fs.Read($buf, 0, $blk) } catch {
            Say ("READ ERROR na " + [math]::Round($pos/1MB,0) + " MB: " + $_.Exception.Message)
            $pos += $blk; try { $fs.Position = $pos } catch { break }; continue
        }
        if ($n -le 0) { break }
        $pos += $n
        if (($pos - $winStart) -ge ($ZoneStepMB * 1MB)) {
            $sec = $winSw.Elapsed.TotalSeconds
            $mb = ($pos - $winStart)/1MB
            $spd = if ($sec -gt 0) { $mb/$sec } else { 0 }
            $atGB = [math]::Round($pos/1GB,2)
            $results += [pscustomobject]@{ AtGB = $atGB; MBs = [math]::Round($spd,1) }
            $line = "  {0,9} GB  {1,9} MB/s" -f $atGB, [math]::Round($spd,1)
            if ($spd -lt $SlowMBs) { $line += "   <-- PROSADKA"; $slow += [pscustomobject]@{ AtGB = $atGB; MBs = [math]::Round($spd,1) } }
            Say $line
            $winStart = $pos; $winSw.Restart()
        }
    }
}
$fs.Close()

Say "----------------------------------------------------------"
if ($results.Count -gt 0) {
    $st = $results.MBs | Measure-Object -Minimum -Maximum -Average
    Say ("Tochek/okon: " + $results.Count + "   min " + [math]::Round($st.Minimum,1) + " / avg " + [math]::Round($st.Average,1) + " / max " + [math]::Round($st.Maximum,1) + " MB/s")
    $b1 = ($results | Where-Object { $_.MBs -lt 100 }).Count
    $b2 = ($results | Where-Object { $_.MBs -ge 100 -and $_.MBs -lt 500 }).Count
    $b3 = ($results | Where-Object { $_.MBs -ge 500 -and $_.MBs -lt 1500 }).Count
    $b4 = ($results | Where-Object { $_.MBs -ge 1500 }).Count
    Say ("Raspredelenie:  <100 MB/s: $b1   100-500: $b2   500-1500: $b3   >1500: $b4")
}
Say ("Prosadok nizhe ${SlowMBs} MB/s: " + $slow.Count)
foreach ($s in ($slow | Select-Object -First 60)) { Say ("   " + $s.AtGB + " GB : " + $s.MBs + " MB/s") }
Say "DONE"
$sw.Close()