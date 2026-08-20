$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Чем LEDKeeper2 (32-бит) достаёт видеокарту: DLL процесса + свежие конфиги подсветки.
#
# Грабля (СЗ 161190): Get-Process.Modules из SYSTEM-агента не перечисляет модули 32-битного
# процесса — вывод пустой, и легко решить, что GPU-библиотек нет. Берём tasklist /m, он
# показывает честно. Гипотеза: RGB-софт опрашивает видеокарту по I2C через nvapi (ищет
# подсвеченные VGA) — и сам опрос будит карту в P0, хотя подсветки у Gigabyte WINDFORCE нет.
#
#   szcli exec <СЗ> -f tools\recipes\client\ledkeeper-gpu-probe.ps1

foreach ($n in 'LEDKeeper2','LightKeeperService','Mystic_Light_Service','DCv2') {
    $p = Get-Process $n -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { "== $n — не запущен"; continue }
    "== {0} (pid {1}, {2}-бит)" -f $n, $p.Id, $(if ([Environment]::Is64BitOperatingSystem) { '?' } else { '32' })
    $mods = tasklist /m /fi "PID eq $($p.Id)" 2>$null
    $line = ($mods | Where-Object { $_ -match [regex]::Escape($n) }) -join ' '
    $gpu = @()
    foreach ($m in 'nvapi.dll','nvapi64.dll','nvml.dll','d3d9.dll','d3d11.dll','dxgi.dll','opengl32.dll','nvd3dum','nvcuda') {
        if ($line -match [regex]::Escape($m)) { $gpu += $m }
    }
    if ($gpu) { "   GPU-библиотеки: " + ($gpu -join ', ') } else { "   GPU-библиотек не видно" }
    "   всего модулей в строке: " + (($line -split ',').Count)
}

'== свежие конфиги подсветки (что менялось последним)'
foreach ($r in "$env:ProgramData\MSI\MSI Center", "${env:ProgramFiles(x86)}\MSI\MSI Center\Mystic Light") {
    if (-not (Test-Path $r)) { continue }
    Get-ChildItem $r -Recurse -File -Include '*.dat','*.json','*.xml','*.ini' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 10 |
        ForEach-Object { "   {0:dd.MM.yyyy HH:mm}  {1,8:N0} КБ  {2}" -f $_.LastWriteTime, ($_.Length/1KB), $_.FullName.Replace($r,'…') }
}

'== устройства подсветки, которые видит система (I2C/SMBus-адаптеры GPU)'
Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'I2C|SMBus|LED|RGB' } |
    ForEach-Object { "   {0,-52} {1}" -f $_.Name, $_.Status }
