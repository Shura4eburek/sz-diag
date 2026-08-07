$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Грабля (бэклог п.96, СЗ 161312): на неверный TestType OCCT CLI отвечает
#   Error : Could not load the schedule file - file does not exists
# при том, что файл на месте и читается. Это ошибка РАЗБОРА, а не путей — полчаса ушло на
# проверку кавычек, прав и путей, прежде чем это выяснилось перебором.
#
# Отдельного теста памяти в CLI нет вовсе: Memory / MemoryOcct / Ram / MemoryTest — все дают
# ту же ложь про файл. Память гоняется через CpuLinpack с явным Memory (>= 75 % ОЗУ) либо
# через Combined.
#
#   szcli exec <СЗ> -f tools\recipes\client\check-occt-schedule.ps1
# Без param(): `szcli exec -f` клеит скрипт после своей шапки (кодировка + $ProgressPreference),
# а param-блок обязан быть первым выражением — иначе на клиенте «The term 'param' is not
# recognized» и скрипт молча работает мимо аргументов (260306). Путь задаётся переменной
# $Schedule, если её выставили выше по скрипту; иначе ищется рядом с агентом.
if (-not (Get-Variable Schedule -Scope Script -ErrorAction SilentlyContinue)) { $Schedule = $null }

# Проверено перебором на 161312: работает только это.
$valid = @('CpuOnlyOcct', 'PowerSupply', 'CpuLinpack', 'Combined', 'CpuOcct', 'Memtest', 'Vram', 'Gpu3d', 'GpuUnreal')

if (-not $Schedule) {
    $proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
    $occt = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'
    $Schedule = Join-Path $occt 'schedule-gpu.json'
}

if (-not (Test-Path $Schedule)) { "НЕТ ФАЙЛА: $Schedule"; exit 1 }

$json = Get-Content $Schedule -Raw -Encoding UTF8 | ConvertFrom-Json
$bad = @()
$i = 0
foreach ($p in $json.Periods) {
    $i++
    $line = "{0,2}. {1,-14} {2}" -f $i, $p.TestType, $p.Duration
    if ($valid -contains $p.TestType) {
        # CpuLinpack с дефолтными 2048 МБ памяти «тестом памяти» не является: из коробки он
        # её почти не трогает. Для проверки памяти нужно >= 75 % ОЗУ.
        if ($p.TestType -eq 'CpuLinpack') {
            $mem = $p.CpuLinpackConfig.Memory
            $totalMb = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1MB)
            $share = if ($totalMb) { [math]::Round(100 * $mem / $totalMb) } else { 0 }
            $line += "   Memory=$mem МБ ($share % ОЗУ)"
            if ($share -lt 75) { $line += "  <-- ПАМЯТЬ ПОЧТИ НЕ ГРУЗИТСЯ, подними Memory" }
        }
        $line
    } else {
        $bad += $p.TestType
        "$line   <-- НЕДОПУСТИМЫЙ TestType"
    }
}

if ($bad.Count -gt 0) {
    ""
    "Недопустимые TestType: $($bad -join ', ')"
    "Доступны: $($valid -join ', ')"
    "ВАЖНО: OCCT на такой файл ответит 'Could not load the schedule file - file does not exists' —"
    "это ЛОЖЬ про путь, на самом деле он не понял TestType."
    "Тест памяти: отдельного нет, гонять CpuLinpack с Memory >= 75 % ОЗУ либо Combined."
    exit 1
}

""
"Расписание валидно."
exit 0
