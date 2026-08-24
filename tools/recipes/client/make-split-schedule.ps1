$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Расписание-РАЗДЕЛИТЕЛЬ ветки для «немого» hard-off (0 BSOD, 0 WHEA). Породила СЗ 161498:
# 7800X3D + RTX 5080 + УЦЕНЁННЫЙ БЖ 850 Вт + EXPO 6000 — три подозреваемых сразу, и обычный
# Combined/PowerSupply валит машину, но НЕ говорит, кто виноват.
#
# Идея: фазы идут по возрастанию ПОТРЕБЛЕНИЯ, а не по «важности». Момент вырубона = вердикт:
#   фаза 1-2 (CPU без GPU, ~200 Вт по системе) → БЖ на 850 Вт исключён арифметикой, это IMC/память/CPU;
#   фаза 3 (CPU+GPU, пик ~600-700 Вт)          → питание/транзиенты, ветка БЖ.
# Порядок именно такой: начни с PowerSupply — и разделения не будет, машина ляжет на первой фазе.
#
# Валидные TestType: Combined, PowerSupply, CpuOcct, CpuOnlyOcct, CpuLinpack, Memtest, Vram, Gpu3d, GpuUnreal.
#   szcli exec <СЗ> -f tools\recipes\client\make-split-schedule.ps1

$plan = @(
    @{ Type = 'CpuOnlyOcct'; Dur = '00:20:00' },   # только ядра: максимум тепла/тока CPU, GPU в простое
    @{ Type = 'CpuOcct';     Dur = '00:20:00' },   # + большой набор данных: сюда попадает IMC и ОЗУ
    @{ Type = 'PowerSupply'; Dur = '00:25:00' }    # CPU+GPU одновременно: пиковое потребление сборки
)

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $proc) { throw 'агент не найден — не от чего считать путь к tools\occt' }
$occt = @(
    (Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'),
    (Join-Path $env:ProgramData 'szdiag\tools\occt')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $occt) { throw 'tools\occt нет ни рядом с агентом, ни в ProgramData — сначала szcli push <СЗ> occt' }

$donorFile = @('schedule-long.json', 'schedule.json') |
    ForEach-Object { Join-Path $occt $_ } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $donorFile) { throw "нет эталонного расписания OCCT в $occt — сначала szcli push <СЗ> occt" }

$sched = Get-Content $donorFile -Raw -Encoding UTF8 | ConvertFrom-Json
$donor = $sched.Periods[0]
if (-not $donor) { throw "в $donorFile нет ни одного Period — эталон непригоден как донор" }

# @() обязателен: один период PowerShell отдаёт скаляром, и OCCT врёт «file does not exists».
$sched.Periods = @(foreach ($p in $plan) {
    $c = $donor | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $c.TestType   = $p.Type
    $c.Duration   = $p.Dur
    $c.IsInfinite = $false

    # Донор приходит с Threads=Fixed/1/Normal — тупая копия грузит ОДНО ядро и рапортует
    # «выстоял» (грабля 161346). Оба CPU-конфига правим явно.
    $c.CpuOcctConfig.Mode               = 'Extreme'
    $c.CpuOcctConfig.Threads            = 'Auto'
    $c.CpuOcctConfig.DataSet            = 'Large'
    $c.CpuOcctConfig.LoadType           = 'Variable'
    $c.CpuOcctConfig.OcctInstructionSet = 'Auto'

    $c.CpuOnlyOcctConfig.Mode               = 'Extreme'
    $c.CpuOnlyOcctConfig.Threads            = 'Auto'
    $c.CpuOnlyOcctConfig.DataSet            = 'Small'
    $c.CpuOnlyOcctConfig.LoadType           = 'Variable'
    $c.CpuOnlyOcctConfig.OcctInstructionSet = 'Auto'

    # Switch 20↔100 % — транзиенты на БЖ, то самое, что валит слабое питание
    $c.GpuUnrealConfig.IntensityType = 'Switch'
    $c
})

$out = Join-Path $occt 'schedule-split.json'
$sched | ConvertTo-Json -Depth 20 | Set-Content $out -Encoding UTF8
if (-not (Test-Path $out)) { throw "не удалось записать $out" }
$raw = Get-Content $out -Raw -Encoding UTF8
if ($raw -notmatch '"Periods"\s*:\s*\[') { throw "в $out Periods сериализован не массивом — OCCT такой файл не примет" }

"записан: $out (донор: $(Split-Path $donorFile -Leaf))"
$check = Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json
$total = [TimeSpan]::Zero
$t0 = Get-Date
foreach ($p in $check.Periods) {
    $cfg = switch ($p.TestType) {
        'CpuOcct'     { "$($p.CpuOcctConfig.Mode)/$($p.CpuOcctConfig.Threads)/$($p.CpuOcctConfig.DataSet)" }
        'CpuOnlyOcct' { "$($p.CpuOnlyOcctConfig.Mode)/$($p.CpuOnlyOcctConfig.Threads)/$($p.CpuOnlyOcctConfig.DataSet)" }
        'PowerSupply' { 'CPU+GPU, пик по питанию' }
        default       { '' }
    }
    # Печатаем ОЖИДАЕМОЕ время начала каждой фазы: вырубон по часам сразу ложится в фазу,
    # без этого потом гадаешь, на чём именно легло (сверять с временем старта задачи).
    "   {0,-12} {1}  +{2:hh\:mm}  {3}" -f $p.TestType, $p.Duration, $total, $cfg
    $total += [TimeSpan]::Parse($p.Duration)
}
"ИТОГО: $total, расписание конечное — тест сам остановится, «выстояла» сверять по sensors.csv"
"Границы фаз от старта: CPU-only 0-20 мин | CPU+данные 20-40 мин | CPU+GPU пик 40-65 мин"
