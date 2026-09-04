$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Снести с клиента всё, что мы туда привезли `szcli push`, плюс рабочие папки рецептов.
#
# Грабля (СЗ 160306, бэклог п.158): `szcli client cleanup` снимает драйвер lhmmon и чистит
# `C:\ProgramData\szdiag\{jobs,sensors}` — и на этом всё. Сами тулы (`tools\prime95` 34 МБ,
# `tools\lhmmon` 67 МБ) и `C:\OCCT` с логами прогонов остаются на машине, а `client info` их
# даже не показывает. То есть после «уборки» клиент уезжает с сотней мегабайт наших бинарей,
# что прямо противоречит инварианту «весь доступ откатывается без следов».
#
# Гонять ПЕРЕД `szcli close`, вместе с `client cleanup` (тот снимает драйвер и задачи) и
# ПОСЛЕ `szcli stress stop <СЗ>` (снимает процессы/lhmmon/задачи/драйверы, которые как раз
# и держат эти папки — без него это первый заход из двух, бэклог п.183, СЗ 161346).
# Агент и его `appsettings.json` НЕ трогаем — он сносит себя сам при revert.
#   szcli exec <СЗ> -f tools\recipes\client\wipe-tools.ps1
$ErrorActionPreference = 'SilentlyContinue'

# Бэклог п.183: «файл занят?» ничего не говорит, КЕМ. Проверяем известные держатели папок
# инструментов — те же процессы, что снимает stress stop.
function Show-Holders {
    param($Dir)
    $names = 'OCCTCmd', 'OCCT', 'furmark', 'TM5', '3DMarkCmd', 'prime95', 'y-cruncher', 'Kagari', 'lhmmon'
    $alive = Get-Process $names -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($Dir, [StringComparison]::OrdinalIgnoreCase) }
    if ($alive) {
        '  держит: ' + (($alive | ForEach-Object { "$($_.ProcessName) pid=$($_.Id)" }) -join ', ')
    } else {
        # Minor (ревью волны 2): explorer.exe живёт в C:\Windows — его .Path на путь целевой
        # папки НИКОГДА не совпадёт, раньше он сидел в $names и печатал вводящее в заблуждение
        # «держит: не по пути». Открытое окно проводника проверить по Path нельзя в принципе —
        # подсказываем это отдельно, а не притворяемся, что проверили.
        $explorerHint = if (Get-Process explorer -ErrorAction SilentlyContinue) {
            ' Explorer.exe запущен — возможно, папка открыта в окне проводника (по Path это не проверить).'
        } else { '' }
        "  держит: не по пути (проверь sc query R0lhmmon и szcli stress stop — вдруг не гонялся).$explorerHint"
    }
}

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $proc) { 'агент не найден — путь к tools\ не резолвится, снеси вручную'; return }
$base = Split-Path $proc.ExecutablePath -Parent

# Два места: рядом с агентом и в ProgramData (туда push кладёт, если агент запущен из
# облачной папки вроде OneDrive — бэклог п.151).
$targets = @(
    "$base\tools\prime95", "$base\tools\lhmmon", "$base\tools\ycruncher",
    "$base\tools\occt", "$base\tools\tm5", "$base\tools\furmark", "$base\tools\3dmark",
    'C:\ProgramData\szdiag\tools',
    'C:\OCCT'   # рабочая папка рецептов: логи прогонов, sensors.csv, iotest.bin
)

$freed = 0
foreach ($d in $targets) {
    if (-not (Test-Path $d)) { continue }
    $mb = [math]::Round((Get-ChildItem $d -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Remove-Item $d -Recurse -Force
    if (Test-Path $d) { "⚠ НЕ снято (файл занят): $d"; Show-Holders $d }
    else { "снято: $d ($mb МБ)"; $freed += $mb }
}
if ($freed -eq 0) { 'чисто — сносить нечего' } else { "освобождено: $freed МБ" }

# Приёмка: что осталось в tools\ — там должны быть только штатные файлы агента, не наши тулы.
$rest = (Get-ChildItem "$base\tools" -Directory | Select-Object -ExpandProperty Name) -join ', '
if ($rest) { "⚠ осталось в tools\: $rest" } else { 'tools\ пуста' }
