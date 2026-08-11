$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# GPU-расписание OCCT из штатного эталона: меняются только TestType/Duration, все настройки
# тестов (индекс GPU, потоки, проценты памяти) берутся родные — расписание остаётся портируемым.
# Профиль заточен под hard-off без WHEA: ошибки вычислений → VRAM → транзиенты → пик по питанию.
# Валидные TestType: Combined, PowerSupply, CpuOcct, CpuOnlyOcct, CpuLinpack, Memtest, Vram, Gpu3d, GpuUnreal.
#   szcli exec <СЗ> -f tools\recipes\client\make-gpu-schedule.ps1

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$occt = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'

$plan = @(
    @{ Type = 'Gpu3d';       Dur = '00:20:00' },   # ErrorDetection — ловит ошибки вычислений/артефакты
    @{ Type = 'Vram';        Dur = '00:15:00' },   # 90 % памяти видеокарты
    @{ Type = 'GpuUnreal';   Dur = '00:25:00' },   # Switch 20↔100 % каждые 500 мс — транзиенты по БЖ
    @{ Type = 'PowerSupply'; Dur = '00:30:00' }    # CPU+GPU одновременно, пиковое потребление
)

# Донор — штатное расписание OCCT. Имя файла зависит от того, чем его завели: свежая
# раздача (`szcli push occt`) кладёт `schedule.json`, ручные прогоны оставляли
# `schedule-long.json`. На 260306 жёсткое имя `schedule-long.json` дало пустой JSON и
# ложное «записан» — берём первое, что реально лежит, иначе падаем громко.
$donorFile = @('schedule-long.json', 'schedule.json') |
    ForEach-Object { Join-Path $occt $_ } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $donorFile) { throw "нет эталонного расписания OCCT в $occt (ожидались schedule-long.json / schedule.json) — запусти OCCT один раз вручную или сделай szcli push <СЗ> occt" }

$sched = Get-Content $donorFile -Raw -Encoding UTF8 | ConvertFrom-Json
$donor = $sched.Periods[0]
if (-not $donor) { throw "в $donorFile нет ни одного Period — эталон непригоден как донор" }
# @() обязателен: план из одного периода PowerShell отдаёт скаляром, и OCCT на такой JSON
# отвечает «Could not load the schedule file - file does not exists» (161346)
$sched.Periods = @(foreach ($p in $plan) {
    $c = $donor | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $c.TestType = $p.Type
    $c.Duration = $p.Dur
    $c.IsInfinite = $false
    $c.GpuUnrealConfig.IntensityType = 'Switch'
    $c
})
$out = Join-Path $occt 'schedule-gpu.json'
$sched | ConvertTo-Json -Depth 20 | Set-Content $out -Encoding UTF8
if ((Get-Content $out -Raw -Encoding UTF8) -notmatch '"Periods"\s*:\s*\[') { throw "в $out Periods сериализован не массивом — OCCT такой файл не примет" }
# «Записан» печатаем только по факту наличия файла: на 260306 сообщение об успехе было,
# а файла на клиенте не оказалось — и это выяснилось лишь при следующем запуске.
if (-not (Test-Path $out)) { throw "не удалось записать $out" }

"записан: $out (донор: $(Split-Path $donorFile -Leaf))"
$total = [TimeSpan]::Zero
foreach ($p in $sched.Periods) { "   {0,-12} {1}" -f $p.TestType, $p.Duration; $total += [TimeSpan]::Parse($p.Duration) }
# Конечное расписание САМО завершает тест — снаружи это неотличимо от «машина выстояла» (грабля 160306)
"ИТОГО: тест сам завершится через $total — «выстояла N часов» сверять по ряду сенсоров, а не по факту «тест идёт»"
