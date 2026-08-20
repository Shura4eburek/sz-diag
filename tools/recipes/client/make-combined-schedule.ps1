$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Расписание OCCT Combined нужной длительности — CPU + RAM + GPU одновременно.
# Это режим максимума ТРАНЗИЕНТОВ: нагрузка скачет, и именно на переходах вылезают
# слабое питание и нестабильный IMC (159873: Combined воспроизвёл дефект за минуты
# там, где 5 часов RDR2 не смогли). Берётся, когда времени на раздельные CPU/GPU/MEM
# прогоны нет, а сказать «сборка держит пик» надо.
# Штатный schedule-long.json — уже Combined, но на 1:30; рецепт делает копию с нужным
# временем и печатает СОСТАВ (какие подтесты реально включены), чтобы не гадать потом,
# грузился ли GPU.
#   szcli exec <СЗ> -f tools\recipes\client\make-combined-schedule.ps1

$Duration = '00:45:00'   # ← сколько гнать

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $proc) { throw 'агент не найден — не от чего считать путь к tools\occt' }
$occt = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'

$donorFile = @('schedule-long.json', 'schedule.json') |
    ForEach-Object { Join-Path $occt $_ } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $donorFile) { throw "нет эталонного расписания OCCT в $occt — сначала szcli push <СЗ> occt" }

$sched = Get-Content $donorFile -Raw -Encoding UTF8 | ConvertFrom-Json
# Донором берём именно Combined-период, если он в эталоне есть: у остальных типов
# CombinedConfig может быть выключенной заглушкой.
$donor = @($sched.Periods | Where-Object { $_.TestType -eq 'Combined' })[0]
if (-not $donor) { $donor = $sched.Periods[0] }
if (-not $donor) { throw "в $donorFile нет ни одного Period — эталон непригоден как донор" }

# @() обязателен: один период сериализуется объектом, и OCCT врёт «файла не существует».
$sched.Periods = @(
    $c = $donor | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $c.TestType   = 'Combined'
    $c.Duration   = $Duration
    $c.IsInfinite = $false
    $c
)

$out = Join-Path $occt 'schedule-combined.json'
$sched | ConvertTo-Json -Depth 20 | Set-Content $out -Encoding UTF8
$raw = Get-Content $out -Raw -Encoding UTF8
if ($raw -notmatch '"Periods"\s*:\s*\[') { throw "в $out Periods сериализован не массивом — OCCT такой файл не примет" }

$check = (Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json).Periods[0]
"записан: $out (донор: $(Split-Path $donorFile -Leaf))"
"   Combined $($check.Duration)"
$cc = $check.CombinedConfig
foreach ($n in 'CpuOcct','CpuOnlyOcct','CpuLinpack','Memory','Gpu3d','GpuUnreal','GpuCompute','Vram') {
    "   {0,-12} {1}" -f $n, $(if ($cc.$n) { 'вкл' } else { '-' })
}
if (-not ($cc.GpuUnreal -or $cc.Gpu3d -or $cc.Vram)) { '⚠ GPU в этом Combined выключен — питание под полным пиком не проверится' }
if (-not $cc.Memory) { '⚠ Memory в этом Combined выключен — ОЗУ грузиться не будет' }
