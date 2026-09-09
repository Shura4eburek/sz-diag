$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Найти, куда OCCT положил отчёт прогона, и что в нём по ошибкам.
#
# Грабля (СЗ 162731): `szcli test result` ищет occt-report.html в рабочей папке шага,
# а OCCT под SYSTEM/в сессии пользователя пишет результат в свой каталог (Documents\OCCT
# или %LOCALAPPDATA%\Temp\OCCT). Без этого прогон выглядит «не завершился», хотя отработал.
$roots = @(
    "$env:USERPROFILE\Documents\OCCT",
    "C:\Users\User\Documents\OCCT",
    "C:\Users\User\AppData\Local\Temp\OCCT",
    "C:\Client-test\tools\occt",
    "C:\Windows\SysWOW64\config\systemprofile\Documents\OCCT",
    "C:\Windows\System32\config\systemprofile\Documents\OCCT"
)
foreach ($r in $roots) {
    if (-not (Test-Path $r)) { continue }
    "== $r"
    Get-ChildItem $r -Recurse -Include *.html, *.csv, *.json -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 8 |
        ForEach-Object { "   {0}  {1} б  {2:dd.MM HH:mm:ss}" -f $_.FullName, $_.Length, $_.LastWriteTime }
}

# Ошибки прогона: в report.csv последняя колонка — Total Errors (WHEA), плюс OCCT
# пишет свои ошибки в колонки *Errors*. Считаем максимум по каждой такой колонке.
$csv = Get-ChildItem "C:\Users\User\AppData\Local\Temp\OCCT" -Recurse -Filter report.csv -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($csv) {
    "== разбор $($csv.FullName)"
    $lines = [IO.File]::ReadAllLines($csv.FullName)
    "   строк: $($lines.Count)  (замер раз в секунду -> ~$([math]::Round($lines.Count/60,1)) мин)"
    # Шапка CSV у OCCT двухстрочная: первая — группы, вторая — имена колонок.
    $head = $lines[1] -split ','
    $idx = @()
    for ($i = 0; $i -lt $head.Count; $i++) { if ($head[$i] -match 'Error') { $idx += $i } }
    foreach ($i in $idx) {
        $max = 0
        foreach ($l in $lines[2..($lines.Count - 1)]) {
            $v = ($l -split ',')[$i]
            $n = 0
            if ([double]::TryParse($v, [ref]$n)) { if ($n -gt $max) { $max = $n } }
        }
        "   колонка '$($head[$i])': максимум $max"
    }
    "   первая строка данных: $($lines[2].Substring(0, [Math]::Min(40, $lines[2].Length)))"
    "   последняя строка:     $($lines[-1].Substring(0, [Math]::Min(40, $lines[-1].Length)))"
}
