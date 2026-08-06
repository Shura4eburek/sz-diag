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

$sched = Get-Content (Join-Path $occt 'schedule-long.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$donor = $sched.Periods[0]
$sched.Periods = foreach ($p in $plan) {
    $c = $donor | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $c.TestType = $p.Type
    $c.Duration = $p.Dur
    $c.IsInfinite = $false
    $c.GpuUnrealConfig.IntensityType = 'Switch'
    $c
}
$out = Join-Path $occt 'schedule-gpu.json'
$sched | ConvertTo-Json -Depth 20 | Set-Content $out -Encoding UTF8

"записан: $out"
$total = [TimeSpan]::Zero
foreach ($p in $sched.Periods) { "   {0,-12} {1}" -f $p.TestType, $p.Duration; $total += [TimeSpan]::Parse($p.Duration) }
# Конечное расписание САМО завершает тест — снаружи это неотличимо от «машина выстояла» (грабля 160306)
"ИТОГО: тест сам завершится через $total — «выстояла N часов» сверять по ряду сенсоров, а не по факту «тест идёт»"
