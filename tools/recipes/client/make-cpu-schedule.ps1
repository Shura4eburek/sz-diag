$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# CPU-расписание OCCT из штатного эталона: отдельный прогон стабильности процессора,
# без видеокарты. Породила СЗ 161346 — клиент потребовал проверить процессор отдельно
# (незнятая плёнка на водоблоке + единственный 0x124 WHEA_UNCORRECTABLE_ERROR), а
# Combined/PowerSupply такой ответ не дают: там CPU идёт в паре с GPU и вердикт размазан.
#
# Грабля донора: в штатном schedule.json секция CpuOnlyOcctConfig приходит как
# Threads=Fixed / FixedThreadCount=1 / Mode=Normal — тупая копия донора грузит ОДНО ядро
# и рапортует «выстоял». Поэтому конфиги тестов правятся явно, а не наследуются.
# Валидные TestType: Combined, PowerSupply, CpuOcct, CpuOnlyOcct, CpuLinpack, Memtest, Vram, Gpu3d, GpuUnreal.
#   szcli exec <СЗ> -f tools\recipes\client\make-cpu-schedule.ps1

$LinpackMemMb = 4096   # ← дефолтные 2048 МБ Linpack'а прогревают только кэш (бэклог п.96)

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $proc) { throw 'агент не найден — не от чего считать путь к tools\occt' }
$occt = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'

$plan = @(
    @{ Type = 'CpuOcct';     Dur = '00:45:00' },   # ядра + большой набор данных, Extreme
    @{ Type = 'CpuOnlyOcct'; Dur = '00:30:00' },   # только ядра, максимум тепла и потребления
    @{ Type = 'CpuLinpack';  Dur = '00:40:00' }    # AVX-пик: ток на VRM и сверка результата
)

$donorFile = @('schedule-long.json', 'schedule.json') |
    ForEach-Object { Join-Path $occt $_ } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $donorFile) { throw "нет эталонного расписания OCCT в $occt — сначала szcli push <СЗ> occt" }

$sched = Get-Content $donorFile -Raw -Encoding UTF8 | ConvertFrom-Json
$donor = $sched.Periods[0]
if (-not $donor) { throw "в $donorFile нет ни одного Period — эталон непригоден как донор" }

# @() обязателен: расписание из ОДНОГО периода PowerShell отдаёт скаляром, ConvertTo-Json пишет
# объект вместо массива, и OCCT на такой файл отвечает «Could not load the schedule file - file
# does not exists» — то есть врёт про отсутствие файла (161346, догон Linpack умер за минуту).
$sched.Periods = @(foreach ($p in $plan) {
    $c = $donor | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $c.TestType   = $p.Type
    $c.Duration   = $p.Dur
    $c.IsInfinite = $false

    # CPU с данными: Extreme на всех потоках, набор инструкций максимальный из доступных
    $c.CpuOcctConfig.Mode               = 'Extreme'
    $c.CpuOcctConfig.Threads            = 'Auto'
    $c.CpuOcctConfig.DataSet            = 'Large'
    $c.CpuOcctConfig.LoadType           = 'Variable'
    $c.CpuOcctConfig.OcctInstructionSet = 'Auto'

    # Только ядра: донор приходит на одном потоке в Normal — правим оба поля
    $c.CpuOnlyOcctConfig.Mode               = 'Extreme'
    $c.CpuOnlyOcctConfig.Threads            = 'Auto'
    $c.CpuOnlyOcctConfig.DataSet            = 'Small'
    $c.CpuOnlyOcctConfig.LoadType           = 'Variable'
    $c.CpuOnlyOcctConfig.OcctInstructionSet = 'Auto'

    $c.CpuLinpackConfig.Threads = 'PhysicalAndVirtual'
    $c.CpuLinpackConfig.Memory  = $LinpackMemMb
    $c
})

$out = Join-Path $occt 'schedule-cpu.json'
$sched | ConvertTo-Json -Depth 20 | Set-Content $out -Encoding UTF8
if (-not (Test-Path $out)) { throw "не удалось записать $out" }
# Приёмка формата: Periods обязан быть массивом даже из одного элемента, иначе OCCT молча не стартует
$raw = Get-Content $out -Raw -Encoding UTF8
if ($raw -notmatch '"Periods"\s*:\s*\[') { throw "в $out Periods сериализован не массивом — OCCT такой файл не примет" }

"записан: $out (донор: $(Split-Path $donorFile -Leaf))"
$check = Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json
$total = [TimeSpan]::Zero
foreach ($p in $check.Periods) {
    $cfg = switch ($p.TestType) {
        'CpuOcct'     { "$($p.CpuOcctConfig.Mode)/$($p.CpuOcctConfig.Threads)/$($p.CpuOcctConfig.DataSet)" }
        'CpuOnlyOcct' { "$($p.CpuOnlyOcctConfig.Mode)/$($p.CpuOnlyOcctConfig.Threads)/$($p.CpuOnlyOcctConfig.DataSet)" }
        'CpuLinpack'  { "$($p.CpuLinpackConfig.Threads)/$($p.CpuLinpackConfig.Memory) МБ" }
        default       { '' }
    }
    "   {0,-12} {1}  {2}" -f $p.TestType, $p.Duration, $cfg
    $total += [TimeSpan]::Parse($p.Duration)
}
"ИТОГО: расписание конечное, тест сам остановится через $total — «выстоял» сверять по ряду сенсоров"
