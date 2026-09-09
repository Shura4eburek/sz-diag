$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Снять из расписания OCCT жёстко зашитый набор инструкций (Avx512) там, где CPU его не умеет.
#
# Грабля (СЗ 162731): в deploy/occt/schedule.json период Combined зашит на
# "OcctInstructionSet": "Avx512", а на Intel Core Ultra 7 265K (Arrow Lake) AVX-512 нет
# вообще. Проверить это из PS 5.1 нельзя (типов System.Runtime.Intrinsics нет), а OCCT
# при неподдерживаемом наборе может не отработать период молча — прогон уходит в трубу,
# и это читается как «дефект не воспроизводится».
#
# Ставим "Auto" — OCCT сам выберет максимум, который тянет процессор (AVX2 на Arrow Lake).
# Оригинал сохраняется рядом (schedule.json.instr-orig).
$Restore = $false   # ← true: вернуть оригинальное расписание

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw 'агент не найден — не от чего считать путь к tools\occt' }
$dir  = Join-Path (Split-Path $proc.ExecutablePath -Parent) 'tools\occt'
$cur  = Join-Path $dir 'schedule.json'
$orig = "$cur.instr-orig"

if ($Restore) {
    if (-not (Test-Path $orig)) { throw "нет резервной копии $orig" }
    Copy-Item $orig $cur -Force
    Remove-Item $orig -Force
    "восстановлено оригинальное расписание"
} else {
    if (-not (Test-Path $cur)) { throw "нет $cur — сначала szcli push <СЗ> occt" }
    if (-not (Test-Path $orig)) { Copy-Item $cur $orig -Force; "оригинал сохранён: $orig" }
    # Текстовая замена, а не ConvertTo-Json: расписание вложено глубже, чем депт по умолчанию
    # в PS 5.1, и пересборка JSON молча срезает хвост конфигов ядер.
    $raw = Get-Content $cur -Raw
    $new = $raw -replace '("OcctInstructionSet":\s*)"Avx512"', '$1"Auto"'
    if ($new -eq $raw) { "Avx512 в расписании не найден — менять нечего" }
    else { [IO.File]::WriteAllText($cur, $new, [Text.UTF8Encoding]::new($true)); "Avx512 -> Auto" }
}

# Приёмка: печатаем то, что реально будет выполняться.
$d = Get-Content $cur -Raw | ConvertFrom-Json
foreach ($p in $d.Periods) {
    "   $($p.TestType)  $($p.Duration)  instr=$($p.CpuOcctConfig.OcctInstructionSet)"
}
