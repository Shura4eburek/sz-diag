$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Парный откат к prep-stress/start-sensors/start-occt. Каждый шаг — в своём try/catch и
# идемпотентен: упавший шаг не должен прекращать откат остальных (как в RevertCoordinator).
# Драйвер R0lhmmon после убийства процесса остаётся загруженным и с ключом в реестре —
# на клиентской машине это осталось бы навсегда (бэклог п.88).
# ⚠ CSV забрать ДО запуска: скрипт удаляет C:\OCCT.
#   szcli exec <СЗ> -f tools\recipes\client\cleanup-stress.ps1
$Sz          = '000000'   # ← номер СЗ
$RemoveCsv   = $true      # C:\OCCT удалить (сначала szcli pull!)
# Суффиксы задач (szdiag-<суффикс>-<СЗ>): p95 и yc заведены рецептами start-prime95/start-ycruncher —
# без них задача остаётся на клиенте и снова поднимет тест после ребута (160587).
# 'occt' — базовая задача-донор: её создаёт и штатный прогон, и ручной запуск OCCT (161716).
# 'furmark' — задача из start-furmark.ps1 (161190): запускается в сессии пользователя, поэтому
# после ребута её видно не сразу, но остаётся она так же намертво, как и остальные.
$TaskSuffixes = @('lhm', 'occt', 'occtgpu', 'watch', 'p95', 'yc', 'furmark')

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$base = Split-Path $proc.ExecutablePath -Parent

foreach ($s in $TaskSuffixes) {
    $t = "szdiag-$s-$Sz"
    try {
        $out = schtasks /query /tn $t 2>&1
        if ($LASTEXITCODE -eq 0) { schtasks /end /tn $t 2>&1 | Out-Null; schtasks /delete /tn $t /f 2>&1 | Out-Null; "задача $t снята" }
    } catch { "задача $t : $($_.Exception.Message)" }
}

foreach ($n in @('OCCTCmd', 'OCCTEnterprise', 'lhmmon', 'GPU3DDX11', 'FurMark', 'prime95')) {
    try { Get-Process $n -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); "процесс $n убит" } } catch {}
}

# y-cruncher по имени не ищется: `y-cruncher.exe` — лаунчер, считает дочерний бинарь из
# Binaries\ с именем под конкретный CPU (на Zen4 7500F это «22-ZN4 ~ Kizuna.exe»), см. start-ycruncher.ps1.
try {
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like '*\ycruncher\*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; "процесс $($_.Name) (y-cruncher) убит" }
} catch {}

# Ring0-драйверы сенсоров. Имя НЕ одно: старая сборка lhmmon (LibreHardwareMonitorLib
# 0.8.x) ставила `R0lhmmon`, новая (0.9.6, собрана 15.09 — tools\lhmmon) грузит драйвер
# HWiNFO — служба `HWiNFO_<версия>` с .sys в `C:\Windows\Temp`; его же использует OCCT.
# Искать по фиксированному имени нельзя: на 162003 `sc query R0lhmmon` отвечал «нет такой
# службы» при живом `HWiNFO_206` (Running), то есть откат считал машину чистой, а
# kernel-драйвер оставался на ней навсегда (бэклог п.88, п.273).
$driverNames = @()
try {
    $driverNames += @(Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^R0lhmmon$|^WinRing0|^HWiNFO_' } |
        Select-Object -ExpandProperty Name)
} catch {}
# Плюс имена из реестра: динамически загруженный драйвер бывает виден только там.
try {
    $driverNames += @(Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^R0lhmmon$|^WinRing0|^HWiNFO_' } |
        Select-Object -ExpandProperty PSChildName)
} catch {}
$driverNames = $driverNames | Sort-Object -Unique
if (-not $driverNames) { 'ring0-драйверов сенсоров не найдено' }
foreach ($drv in $driverNames) {
    try {
        $st = sc.exe query $drv 2>&1
        if ($st -match 'STATE') {
            sc.exe stop $drv 2>&1 | Out-Null
            Start-Sleep -Seconds 2
        }
        sc.exe delete $drv 2>&1 | Out-Null
        "драйвер $drv остановлен и удалён"
    } catch { "${drv}: $($_.Exception.Message)" }
    if (Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\$drv") { "⚠ ключ $drv в реестре ещё есть" }
}
# Сам .sys остаётся в Temp и без службы: файл мусорный, но это наш след. Папка зависит от
# того, под кем шёл захват: под SYSTEM это C:\Windows\Temp, из задачи в сеансе пользователя —
# его %LOCALAPPDATA%\Temp (162003: путь драйвера был \??\C:\Users\ADMINI~1\AppData\Local\
# Temp\HWiNFO_x64_206.sys, и чистка только по C:\Windows\Temp его бы не нашла).
$sysDirs = @('C:\Windows\Temp', $env:TEMP)
try {
    $sysDirs += @(Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'AppData\Local\Temp' })
} catch {}
foreach ($d in ($sysDirs | Where-Object { $_ -and (Test-Path $_) } | Sort-Object -Unique)) {
    foreach ($mask in '*HWiNFO*.sys', '*WinRing0*.sys') {
        try {
            Get-ChildItem $d -Filter $mask -ErrorAction SilentlyContinue |
                ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue; "удалён $($_.FullName)" }
        } catch {}
    }
}

try { Remove-MpPreference -ExclusionPath (Join-Path $base 'tools\lhmmon') -ErrorAction Stop; 'исключение Defender снято' }
catch { "Defender: $($_.Exception.Message)" }

if ($RemoveCsv -and (Test-Path 'C:\OCCT')) {
    Remove-Item 'C:\OCCT' -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path 'C:\OCCT') { '⚠ C:\OCCT не удалилась' } else { 'C:\OCCT удалена' }
}

'== осталось от нас'
(schtasks /query /fo csv /nh) -split "`r?`n" | Where-Object { $_ -match "szdiag.*$Sz" } | ForEach-Object { '   ' + ($_ -split '","')[0].Trim('"') }
