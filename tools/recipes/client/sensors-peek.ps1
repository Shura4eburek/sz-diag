$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Быстрый срез сенсоров прямо на клиенте, без забора CSV на хост.
# Породила СЗ 160587: под стрессом нужно видеть температуры «сейчас», а `szcli pull` +
# `sensors report` — это цикл на минуты, и он отвечает на другой вопрос («сколько держалась
# нагрузка»), а не «сколько градусов прямо в эту секунду».
# Печатает по каждой интересной колонке: текущее, максимум и среднее за весь прогон.
#   szcli exec <СЗ> -f tools\recipes\client\sensors-peek.ps1
# Парный к check-load.ps1, но отвечает на другой вопрос: тот показывает хвост («что сейчас»),
# а этот — МАКСИМУМ за весь прогон. Для вердикта о перегреве важен именно пик: хвост в 20
# замеров легко попадает в паузу между FFT-размерами и занижает температуру.
$WaitSec = 0    # ← дать нагрузке прогреться перед срезом

if ($WaitSec -gt 0) { Start-Sleep -Seconds $WaitSec }

# Оба места: `szcli sensors start` пишет в ProgramData, ручной lhmmon — в C:\OCCT.
$csv = Get-ChildItem 'C:\ProgramData\szdiag\sensors\*.csv', 'C:\OCCT\sensors.csv' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $csv) { 'CSV сенсоров не найден — сначала szcli sensors start <СЗ> или start-sensors.ps1'; return }
"файл: $($csv.Name), {0:N0} б, обновлён {1:HH:mm:ss} (назад {2:mm\:ss})" -f `
    $csv.Length, $csv.LastWriteTime, ((Get-Date) - $csv.LastWriteTime)

# Разделитель определяем по шапке: штатный наблюдатель пишет `;` (по локали), lhmmon — `,`.
# С неверным разделителем Import-Csv молча отдаёт одну колонку целиком строкой, и срез
# рассыпается на приведении к double — выглядит как «сенсоров нет».
$head = Get-Content $csv.FullName -TotalCount 1
$delim = if (($head -split ';').Count -gt ($head -split ',').Count) { ';' } else { ',' }
$rows = Import-Csv $csv.FullName -Delimiter $delim
"строк: $($rows.Count), разделитель '$delim'"
if ($rows.Count -lt 2) { 'данных ещё нет'; return }

$first = $rows[0].PSObject.Properties.Name | Select-Object -First 1
"окно: {0} → {1}" -f $rows[0].$first, $rows[-1].$first

# Колонки лезут в срез по смыслу, а не все подряд: полный CSV с LHM — это сотни столбцов,
# и на экране от них не остаётся ничего читаемого.
# Имена колонок разные у двух источников: у lhmmon `CPU|Temperature|Core (Tctl/Tdie)`,
# у штатного наблюдателя `cpu_temp_c`. Ловим оба набора одной регуляркой.
$cols = $rows[0].PSObject.Properties.Name | Where-Object {
    $_ -match 'Temperature|Tctl|Power|Fan|Clock|Load|_temp_|_pct|_w$' -and $_ -notmatch 'Distance to TjMax'
}
'--- сенсор: сейчас / макс / среднее ---'
foreach ($c in $cols) {
    $vals = foreach ($r in $rows) {
        $v = $r.$c
        if ($v -and $v -ne '') { [double]($v -replace ',', '.') }
    }
    if (-not $vals) { continue }
    $m = $vals | Measure-Object -Maximum -Average
    # Мусорные колонки (вечный ноль) отсеиваем: LHM отдаёт кучу неподключённых сенсоров.
    if ($m.Maximum -eq 0) { continue }
    "{0,-48} {1,8:N1} {2,8:N1} {3,8:N1}" -f $c, $vals[-1], $m.Maximum, $m.Average
}
