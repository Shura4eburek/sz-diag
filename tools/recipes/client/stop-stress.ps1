$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Быстро снять нагрузку, чтобы агент снова начал отвечать на команды.
#
# Грабля (СЗ 161346): под OCCT + дисковым стрессом агент перестаёт принимать `exec` (таймаут
# на ack) — а чтобы снять нагрузку, нужен как раз `exec`. Замкнутый круг: единственное окно —
# успеть проскочить между тактами. Поэтому скрипт максимально короткий и бьёт всё разом,
# без опросов и ожиданий.
#
# Наблюдатель сенсоров (lhmmon) НЕ трогаем: он лёгкий, а его ряд нужен непрерывным.
$killed = @()

foreach ($n in 'OCCTCmd', 'OCCT', 'furmark', 'TM5', '3DMarkCmd') {
    $p = Get-Process $n -ErrorAction SilentlyContinue
    if ($p) { $p | Stop-Process -Force -ErrorAction SilentlyContinue; $killed += "$n x$($p.Count)" }
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
