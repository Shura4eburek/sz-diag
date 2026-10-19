$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# КАК НА САМОМ ДЕЛЕ ЗОВЁТСЯ KERNEL-ДРАЙВЕР, КОТОРЫЙ ПОДНЯЛ lhmmon.
#
# Грабля (СЗ 162003, 15.09.2026): рецепты и `szcli client cleanup` ищут драйвер `R0lhmmon`
# (так его звала старая сборка на LibreHardwareMonitorLib 0.8.x). Новый lhmmon собран на
# 0.9.6, и `sc query R0lhmmon` отвечает `FAILED 1060: сервис не существует`, хотя сенсоры
# читаются. Это не косметика: незнакомый нам драйвер НЕ СНИМЕТСЯ при закрытии СЗ и
# останется следом на клиентской машине — ровно то, что инвариант отката запрещает
# (на 161312 загруженный `R0lhmmon` мешал удалить папку с тулами).
#
#   szcli exec <СЗ> -f tools\recipes\client\lhm-driver-name.ps1

'== службы-кандидаты (WinRing0 / R0* / LibreHardwareMonitor)'
$found = @()
foreach ($svc in Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue) {
    if ($svc.Name -match '^R0|WinRing0|Libre|lhm|Ols') {
        '   {0,-24} state={1,-8} start={2,-10} path={3}' -f $svc.Name, $svc.State, $svc.StartMode, $svc.PathName
        $found += $svc.Name
    }
}
if (-not $found) { '   ничего не найдено среди Win32_SystemDriver' }

'== то же через sc query (драйверы, поднятые динамически, иногда видны только так)'
foreach ($name in @('R0lhmmon', 'WinRing0_1_2_0', 'WinRing0x64', 'LibreHardwareMonitor')) {
    $out = (sc.exe query $name 2>&1 | Out-String).Trim()
    if ($out -notmatch '1060') { "   {0}: {1}" -f $name, (($out -split "`n" | Select-String 'STATE' | ForEach-Object { $_.Line.Trim() }) -join ' ') }
    else { "   {0}: нет такой службы" -f $name }
}

'== .sys рядом с lhmmon и в системных папках (что он вообще распаковал)'
$proc = Get-CimInstance Win32_Process -Filter "Name='lhmmon.exe'" | Select-Object -First 1
if ($proc) {
    "   процесс lhmmon: pid $($proc.ProcessId), путь $($proc.ExecutablePath)"
    $dir = Split-Path $proc.ExecutablePath -Parent
    Get-ChildItem $dir -Filter '*.sys' -ErrorAction SilentlyContinue |
        ForEach-Object { '   {0}  {1:N0} б' -f $_.FullName, $_.Length }
} else { '   процесса lhmmon нет' }
Get-ChildItem "$env:TEMP", 'C:\Windows\Temp', 'C:\Windows\System32\drivers' -Filter '*ring0*.sys' -ErrorAction SilentlyContinue |
    ForEach-Object { '   {0}  {1:yyyy-MM-dd HH:mm}' -f $_.FullName, $_.LastWriteTime }
