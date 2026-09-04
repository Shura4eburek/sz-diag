$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Быстро снять нагрузку, чтобы агент снова начал отвечать на команды.
#
# Грабля (СЗ 161346): под OCCT + дисковым стрессом агент перестаёт принимать `exec` (таймаут
# на ack) — а чтобы снять нагрузку, нужен как раз `exec`. Замкнутый круг: единственное окно —
# успеть проскочить между тактами. Поэтому скрипт максимально короткий и бьёт всё разом,
# без опросов и ожиданий.
#
# Наблюдатель сенсоров (lhmmon) НЕ трогаем: он лёгкий, а его ряд нужен непрерывным.
#
# Грабля (СЗ 160306, бэклог п.157): в списке не было `prime95`, хотя `start-prime95.ps1` лежит
# рядом — «стоп» отрабатывал вхолостую, тест продолжал греть машину. Плюс запуск оставляет за
# собой scheduled task `szdiag-<тул>-<СЗ>`: убить процесс мало, задача под SYSTEM остаётся на
# клиентской машине. Оба списка ниже — единственный источник правды, дополнять их вместе с
# каждым новым `start-*.ps1`.
$killed = @()

# y-cruncher переименовывает себя под конкретный CPU (на Zen4 — `19-ZN4 ~ Kagari.exe`),
# поэтому ловим и лаунчер, и известные имена бинарей.
#
# Грабля (161346, 26.08, бэклог п.213): список был ограничен ОБОЛОЧКАМИ стресс-тулов — закрытие
# окна OCCT снимало `OCCTCmd`, но рабочие подтесты (`linpack` — CPU, `gpu3d-Win64-Shipping` —
# GPU) переживали оболочку и молотили ещё 1.5 часа незамеченными (машина считалась свободной,
# а на деле держала полную нагрузку). Список подпроцессов — тоже единственный источник правды,
# дополнять вместе с новыми `start-*.ps1`.
foreach ($n in 'OCCTCmd', 'OCCT', 'furmark', 'TM5', '3DMarkCmd', 'prime95', 'y-cruncher', 'Kagari',
                'linpack', 'gpu3d-Win64-Shipping', 'gpu_unreal', 'memtest*') {
    $p = Get-Process $n -ErrorAction SilentlyContinue
    if ($p) { $p | Stop-Process -Force -ErrorAction SilentlyContinue; $killed += "$n x$($p.Count)" }
}

# Задачи, которыми стресс-тулы запускались под SYSTEM (без этого процесс убит, а задача жива).
#
# R-I3 (ревью волны 1): `szdiag-occt-*` не матчит `szdiag-occtcomb-*` (см. check-occt-result.ps1) —
# процессы добивались, а задача того же прогона оставалась жива и перезапускала их. `szdiag-occt*`
# (без дефиса) ловит оба варианта. lhmmon сюда сознательно НЕ входит (см. заголовок файла).
# Minor (ревью волны 2): `szdiag-ab-*` (apply-ab-profile.ps1) убрана отсюда — это не стресс-
# задача, а разовое применение профиля Afterburner клиента, снимать её вместе со стресс-тулами
# не нужно (и опасно, если она ещё применяет профиль). Добавлена `szdiag-sleepcycle-*` — именно
# она заперла 161498 (ишью #181), а тут её не было. Полный путь задачи (TaskPath+TaskName) —
# без него `schtasks /end/delete` не разрешит задачу из подпапки.
foreach ($t in Get-ScheduledTask -TaskName 'szdiag-p95-*', 'szdiag-yc-*', 'szdiag-occt*', 'szdiag-tm5-*',
                'szdiag-iostress-*', 'szdiag-furmark-*', 'szdiag-game-*', 'szdiag-dw-*', 'szdiag-sleepcycle-*' -ErrorAction SilentlyContinue) {
    $full = $t.TaskPath + $t.TaskName
    schtasks /end /tn $full 2>$null | Out-Null
    schtasks /delete /tn $full /f 2>$null | Out-Null
    $killed += "task $full"
}

# Фоновые задачи szcli exec --detach: бьём только те, что крутят наши стресс-скрипты,
# чтобы не задеть служебные процессы агента.
$jobs = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -match 'szdiag\\jobs' })
foreach ($j in $jobs) {
    Stop-Process -Id $j.ProcessId -Force -ErrorAction SilentlyContinue
    $killed += "job pid=$($j.ProcessId)"
}

if ($killed.Count -eq 0) { 'нагрузки не было — снимать нечего' }
else { 'снято: ' + ($killed -join ', ') }
