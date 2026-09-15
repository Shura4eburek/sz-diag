$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# ГДЕ ЖИВОЙ ПРОГОН OCCT ПИШЕТ РЕЗУЛЬТАТ ПРЯМО СЕЙЧАС + сколько ошибок уже есть.
#
# Грабля (СЗ 162003, 15.09.2026): прогон запущен задачей под SYSTEM, поэтому
# `%LOCALAPPDATA%\Temp\OCCT` — это НЕ профиль залогиненного пользователя, а
# `C:\Windows\System32\config\systemprofile\AppData\Local\Temp\OCCT` (и SysWOW64-двойник).
# `check-occt-result.ps1` ищет по владельцу `explorer.exe` и в этих корнях не смотрит —
# на живом прогоне он отвечает «ничего не найдено», хотя OCCT пишет `report.csv`
# посекундно. Без этого нельзя ответить на главный вопрос до конца прогона:
# ошибки УЖЕ есть или пока чисто.
#
# Итоговый `.html` появляется только при штатном выходе; `report.csv` — по ходу.
#
#   szcli exec <СЗ> -f tools\recipes\client\occt-live-report.ps1

$HoursBack = 8

$roots = @(
    'C:\OCCT',
    'C:\Windows\System32\config\systemprofile\AppData\Local\Temp\OCCT',
    'C:\Windows\SysWOW64\config\systemprofile\AppData\Local\Temp\OCCT',
    'C:\Windows\System32\config\systemprofile\Documents\OCCT',
    'C:\Windows\SysWOW64\config\systemprofile\Documents\OCCT',
    'C:\Windows\Temp',
    "$env:ProgramData\OCCT"
)
$since = (Get-Date).AddHours(-$HoursBack)
$found = @()

foreach ($r in $roots) {
    if (-not (Test-Path $r)) { continue }
    $files = Get-ChildItem $r -Recurse -File -Include 'report.csv', 'LastMonitoringValues.json', '*.html' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $since }
    if (-not $files) { continue }
    "== $r"
    foreach ($f in $files) {
        '   {0:yyyy-MM-dd HH:mm:ss} {1,10:N0} б  {2}' -f $f.LastWriteTime, $f.Length, $f.FullName
        $found += $f
    }
}
if (-not $found) { "следов прогона за $HoursBack ч нет ни в одном из корней:"; $roots | ForEach-Object { "   $_" }; return }

# Ошибки: в report.csv последние колонки — счётчики ошибок теста и WHEA.
$csv = $found | Where-Object { $_.Name -eq 'report.csv' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $csv) { 'report.csv среди найденного нет — по ошибкам сказать нечего'; return }

$lines = [IO.File]::ReadAllLines($csv.FullName)
"== разбор $($csv.FullName)"
"   строк: $($lines.Count) (замер раз в секунду -> ~$([math]::Round($lines.Count/60,1)) мин прогона)"
if ($lines.Count -lt 2) { '   данных ещё нет'; return }

$head = $lines[0].Split(',')
$errCols = @()
for ($i = 0; $i -lt $head.Count; $i++) {
    if ($head[$i] -match 'error|erreur') { $errCols += $i }
}
if (-not $errCols) { '   колонок с ошибками в шапке нет: ' + ($head -join ' | '); return }

foreach ($i in $errCols) {
    $max = 0
    foreach ($l in $lines[1..($lines.Count - 1)]) {
        $c = $l.Split(',')
        if ($i -lt $c.Count) {
            $v = 0
            if ([int]::TryParse(($c[$i] -replace '[^\d-]', ''), [ref]$v)) { if ($v -gt $max) { $max = $v } }
        }
    }
    '   {0}: максимум {1}' -f $head[$i].Trim('"'), $max
}
'   последняя строка: ' + $lines[$lines.Count - 1]
