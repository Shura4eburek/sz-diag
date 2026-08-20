$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Расписание OCCT только по памяти (TestType=Memtest) — контрольный прогон ОЗУ без GPU и без
# кэш-нагрузки. Породила СЗ 161288: TM5 Heavy5opt берёт столько, сколько сам решит (на 32 ГБ
# машине — 1.3 ГБ × 12 потоков ≈ 15.6 ГБ, то есть ПОЛОВИНУ ОЗУ), и вторая половина остаётся
# непроверенной. OCCT Memtest берётся за проценты от всей памяти (95 %) и закрывает дыру.
# Второй мотив: у Memtest нет окон — это единственный memory-тест, который стартует полностью
# удалённо (TM5 ждёт закрытия ~15 алертов руками на машине, бэклог п.182).
# Валидные TestType: Combined, PowerSupply, CpuOcct, CpuOnlyOcct, CpuLinpack, Memtest, Vram, Gpu3d, GpuUnreal.
#   szcli exec <СЗ> -f tools\recipes\client\make-mem-schedule.ps1

$Duration = '01:00:00'   # ← длительность прогона
# 95 % + Priority High (как в штатном доноре) душат машину так, что через szcli exec не проходит
# даже `Get-CimInstance` с таймаутом 300 с — приёмка прогона становится невозможной, остаётся
# только смотреть на hub «стресс: OCCTCmd». 85 % и Normal грузят память не хуже, но оставляют
# машине воздух на ответ (161288, 19.08).
$MemPct   = 85           # ← сколько ОЗУ отдать тесту, %
$Priority = 'Normal'     # ← Normal: иначе клиент не отвечает на exec вообще

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
if (-not $proc) { throw 'агент не найден — не от чего считать путь к tools\occt' }
$occt = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'

$donorFile = @('schedule-long.json', 'schedule.json') |
    ForEach-Object { Join-Path $occt $_ } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $donorFile) { throw "нет эталонного расписания OCCT в $occt — сначала szcli push <СЗ> occt" }

$sched = Get-Content $donorFile -Raw -Encoding UTF8 | ConvertFrom-Json
$donor = $sched.Periods[0]
if (-not $donor) { throw "в $donorFile нет ни одного Period — эталон непригоден как донор" }

# @() обязателен: один период PowerShell отдаёт скаляром, и OCCT отвечает «файла не существует»
# на вполне существующий файл (та же грабля, что в make-cpu-schedule.ps1).
$sched.Periods = @(
    $c = $donor | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $c.TestType   = 'Memtest'
    $c.Duration   = $Duration
    $c.IsInfinite = $false
    $c.MemoryConfig.MemoryInPercent = $MemPct
    $c.MemoryConfig.MemoryMemUnit   = 'Percent'
    $c.MemoryConfig.Threads         = 'Auto'
    $c.MemoryConfig.InstructionSet  = 'Auto'
    $c.MemoryConfig.Priority        = $Priority
    $c
)

$out = Join-Path $occt 'schedule-mem.json'
$sched | ConvertTo-Json -Depth 20 | Set-Content $out -Encoding UTF8
$raw = Get-Content $out -Raw -Encoding UTF8
if ($raw -notmatch '"Periods"\s*:\s*\[') { throw "в $out Periods сериализован не массивом — OCCT такой файл не примет" }

$check = Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json
"записан: $out (донор: $(Split-Path $donorFile -Leaf))"
foreach ($p in $check.Periods) {
    "   {0,-10} {1}  {2} % ОЗУ, потоки {3}, приоритет {4}" -f `
        $p.TestType, $p.Duration, $p.MemoryConfig.MemoryInPercent, $p.MemoryConfig.Threads, $p.MemoryConfig.Priority
}
$os = Get-CimInstance Win32_OperatingSystem
"под тест уйдёт ≈ {0} ГБ из {1} ГБ" -f [math]::Round($os.TotalVisibleMemorySize/1MB*$MemPct/100,1), [math]::Round($os.TotalVisibleMemorySize/1MB,1)
