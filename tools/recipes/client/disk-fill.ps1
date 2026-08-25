$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Заполнение накопителя реальными данными до заданного объёма — подготовка к повторному
# линейному чтению после переустановки Windows.
#
# Грабля (СЗ 161972, 25.08): 21.08 линейное чтение raw-устройством нашло зону с ~464 ГБ,
# где скорость валится 3800 -> 9,6 -> 0 МБ/с. После этого винду переустановили с БЫСТРЫМ
# форматом (SMART: DataUnitsWritten вырос всего на 70 ГБ вместо ~1,86 ТБ при полном).
# Быстрый формат = TRIM на весь объём: контроллер отдаёт пустые LBA нулями из воздуха,
# не обращаясь к NAND, и скан дефектной зоны пролетает на полной скорости. Дефект при этом
# никуда не делся — его просто нечем нащупать. Поэтому перед повтором зону надо ЗАПОЛНИТЬ.
#
# Пишет ТОЛЬКО свои файлы в собственную папку и НЕ трогает ничего чужого. Папку после
# прогона снести руками (см. хвост лога) — до тех пор данные нужны, иначе TRIM вернёт нули.
$Dir       = 'C:\szdiag-fill'
$TargetGB  = 700    # покрыть зону 464 ГБ с запасом
$FileGB    = 8      # размер одного файла
$BlockMB   = 8      # блок записи
$MinFreeGB = 60     # ниже этого свободного места не опускаемся ни при каких
$MaxMinutes= 60
$SlowMBs   = 200    # ниже порога проход считается просадкой и логируется отдельно
$Log       = 'C:\ProgramData\szdiag\disk-fill.log'

New-Item -ItemType Directory -Path (Split-Path $Log) -Force -ErrorAction SilentlyContinue | Out-Null
New-Item -ItemType Directory -Path $Dir -Force -ErrorAction SilentlyContinue | Out-Null
$Log = $Log -replace '\.log$', ("-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")
$sw = [IO.StreamWriter]::new($Log, $false, [Text.UTF8Encoding]::new())
$sw.AutoFlush = $true   # вырубон здесь ожидается — без flush потеряется ровно то, ради чего гнали
function Say { param($m) $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m; $sw.WriteLine($line); Write-Output $line }

# Случайные данные, а не нули: нули контроллер может не класть в NAND вообще.
$buf = New-Object byte[] ($BlockMB * 1MB)
(New-Object Random 161972).NextBytes($buf)

$deadline = (Get-Date).AddMinutes($MaxMinutes)
$writtenGB = 0
$slow = @()
Say "start: cel $TargetGB GB, fajly po $FileGB GB v $Dir"

$idx = 0
while ($writtenGB -lt $TargetGB) {
    if ((Get-Date) -gt $deadline) { Say "STOP: limit $MaxMinutes min"; break }
    $free = (Get-PSDrive C).Free / 1GB
    if ($free -lt ($MinFreeGB + $FileGB)) { Say ("STOP: svobodno {0:N1} GB, porog {1} GB" -f $free, $MinFreeGB); break }

    $idx++
    $path = Join-Path $Dir ("fill-{0:D4}.bin" -f $idx)
    $t0 = Get-Date
    try {
        $fs = [IO.File]::Create($path)
        $blocks = [int](($FileGB * 1GB) / ($BlockMB * 1MB))
        for ($i = 0; $i -lt $blocks; $i++) { $fs.Write($buf, 0, $buf.Length) }
        $fs.Flush($true)   # $true = сбросить на носитель, а не в кэш ОС: меряем диск
        $fs.Close()
    } catch {
        Say ("FAIL na " + $path + " : " + $_.Exception.Message)
        break
    }
    $sec = ((Get-Date) - $t0).TotalSeconds
    $mbs = if ($sec -gt 0) { ($FileGB * 1024) / $sec } else { 0 }
    $writtenGB += $FileGB
    $line = "{0,5} GB  {1,8:N1} MB/s" -f $writtenGB, $mbs
    if ($mbs -lt $SlowMBs) { $slow += $line; Say ($line + "   <-- PROSADKA") } else { Say $line }
}

Say ("itogo zapisano {0} GB, prosadok {1}" -f $writtenGB, $slow.Count)
if ($slow.Count -gt 0) { Say "prosadki:"; $slow | ForEach-Object { Say ("  " + $_) } }
Say "papku $Dir posle chtenija snesti: Remove-Item -Recurse -Force $Dir"
$sw.Close()
