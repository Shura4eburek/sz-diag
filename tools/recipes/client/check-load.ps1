$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Приёмка нагрузки по приборам: «тест запущен» ≠ «железо грузится». Читает хвост lhmmon-CSV
# и печатает текущее/среднее/максимум по ключевым датчикам.
# ВАЖНО: только Import-Csv/ConvertFrom-Csv. Ручной split по запятой врёт — первая ячейка шапки
# склеена с BOM и timestamp, индексы уезжают на единицу, и GPU clock 2210 читается как «temp 1058».
#   szcli exec <СЗ> -f tools\recipes\client\check-load.ps1
# Путь не зашит: `szcli sensors start` кладёт CSV в ProgramData под именем с номером СЗ,
# а ручной lhmmon — в C:\OCCT. Берём самый свежий из обоих мест, иначе рецепт молча читает
# позавчерашний файл (или падает на отсутствующем) и показывает «нагрузки нет».
$Csv = Get-ChildItem 'C:\ProgramData\szdiag\sensors\*.csv', 'C:\OCCT\sensors.csv' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $Csv) { 'CSV сенсоров не найден — наблюдатель не запущен (szcli sensors start)'; return }
"файл: $Csv"
$Tail = 20   # сколько последних замеров усреднять

$rows = (@(Get-Content $Csv -TotalCount 1) + @(Get-Content $Csv -Tail $Tail)) | ConvertFrom-Csv
$cols = $rows[0].PSObject.Properties.Name
$last = $rows[-1]
$want = 'Load\|CPU Total|Power\|Package\||Temperature\|Core \(Tctl|Load\|GPU Core|Power\|GPU Package|Temperature\|GPU Core|Temperature\|GPU Hot|Clock\|GPU Core|Voltage\|.*\+12'

"замер: $($last.($cols[0]))  (в выборке строк: $($rows.Count))"
foreach ($c in $cols) {
    if ($c -match $want) {
        $vals = foreach ($r in $rows) { if ($r.$c) { [double]::Parse($r.$c, [cultureinfo]::InvariantCulture) } }
        $m = $vals | Measure-Object -Maximum -Minimum -Average
        "   {0,-34} тек={1,8:N1} сред={2,8:N1} мин={3,8:N1} макс={4,8:N1}" -f `
            ($c -replace '\|/[^|]*$', '' -replace '^[^|]+\|', ''), [double]$last.$c, $m.Average, $m.Minimum, $m.Maximum
    }
}

$p = Get-Process OCCTCmd -ErrorAction SilentlyContinue
'OCCTCmd: ' + $(if ($p) { "жив, CPU-время $([int]$p.CPU) c, RAM $([int]($p.WorkingSet64/1MB)) МБ" } else { 'НЕТ ПРОЦЕССА — тест кончился или упал' })
if ($p) {
    Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $p.Id } | ForEach-Object { "   ребёнок: $($_.Name)" }
}
"CSV: {0:N0} КБ" -f ((Get-Item $Csv).Length / 1KB)
