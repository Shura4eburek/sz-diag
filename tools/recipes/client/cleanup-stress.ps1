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
$TaskSuffixes = @('lhm', 'occt', 'occtgpu', 'watch', 'p95', 'yc')

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$base = Split-Path $proc.ExecutablePath -Parent

foreach ($s in $TaskSuffixes) {
    $t = "szdiag-$s-$Sz"
    try {
        $out = schtasks /query /tn $t 2>&1
        if ($LASTEXITCODE -eq 0) { schtasks /end /tn $t 2>&1 | Out-Null; schtasks /delete /tn $t /f 2>&1 | Out-Null; "задача $t снята" }
    } catch { "задача $t : $($_.Exception.Message)" }
}

foreach ($n in @('OCCTCmd', 'lhmmon', 'GPU3DDX11', 'FurMark', 'prime95')) {
    try { Get-Process $n -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); "процесс $n убит" } } catch {}
}

# y-cruncher по имени не ищется: `y-cruncher.exe` — лаунчер, считает дочерний бинарь из
# Binaries\ с именем под конкретный CPU (на Zen4 7500F это «22-ZN4 ~ Kizuna.exe»), см. start-ycruncher.ps1.
try {
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like '*\ycruncher\*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; "процесс $($_.Name) (y-cruncher) убит" }
} catch {}

try {
    $st = sc.exe query R0lhmmon 2>&1
    if ($st -match 'STATE') {
        sc.exe stop R0lhmmon 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        sc.exe delete R0lhmmon 2>&1 | Out-Null
        'драйвер R0lhmmon остановлен и удалён'
    }
} catch { "R0lhmmon: $($_.Exception.Message)" }
if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\R0lhmmon') { '⚠ ключ R0lhmmon в реестре ещё есть' }

try { Remove-MpPreference -ExclusionPath (Join-Path $base 'tools\lhmmon') -ErrorAction Stop; 'исключение Defender снято' }
catch { "Defender: $($_.Exception.Message)" }

if ($RemoveCsv -and (Test-Path 'C:\OCCT')) {
    Remove-Item 'C:\OCCT' -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path 'C:\OCCT') { '⚠ C:\OCCT не удалилась' } else { 'C:\OCCT удалена' }
}

'== осталось от нас'
(schtasks /query /fo csv /nh) -split "`r?`n" | Where-Object { $_ -match "szdiag.*$Sz" } | ForEach-Object { '   ' + ($_ -split '","')[0].Trim('"') }
