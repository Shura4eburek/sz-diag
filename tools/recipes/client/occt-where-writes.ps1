$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# КУДА ЭТОТ ПРОГОН OCCT ВООБЩЕ ПИШЕТ: поиск по всему диску вместо списка «известных папок».
#
# Грабля (СЗ 162003, 15.09.2026): прогон живой (3.5 ч, CPU 7000+ c, 12-18 ГБ памяти),
# а ни `report.csv`, ни `LastMonitoringValues.json`, ни `.html` не нашлись НИ В ОДНОМ
# известном корне, включая профиль SYSTEM. Патчить список папок дальше бессмысленно —
# ищем по факту: свежие файлы OCCT-сигнатур по всему `C:` за окно прогона.
#
# Дорого по времени (обход дерева), поэтому окно узкое и расширения перечислены явно.
#
#   szcli exec <СЗ> -f tools\recipes\client\occt-where-writes.ps1 --param HoursBack=5

$HoursBack = 5
$since = (Get-Date).AddHours(-$HoursBack)

'== свежие файлы с OCCT-сигнатурами по всему C: (это может занять минуту)'
$hits = Get-ChildItem C:\ -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $_.LastWriteTime -gt $since -and (
            $_.Name -match '^report.*\.csv$' -or
            $_.Name -match 'LastMonitoringValues' -or
            ($_.Extension -in '.html', '.htm') -or
            ($_.Name -match 'memtest' -and $_.Extension -in '.csv', '.json', '.html')
        )
    } |
    Select-Object -First 60
if ($hits) {
    $hits | ForEach-Object { '   {0:yyyy-MM-dd HH:mm:ss} {1,10:N0} б  {2}' -f $_.LastWriteTime, $_.Length, $_.FullName }
} else {
    '   ничего — прогон не пишет результат в файлы вообще'
}

'== что процесс держит открытым (по рабочему каталогу и командной строке)'
foreach ($n in 'OCCTCmd', 'OCCTEnterprise') {
    $p = Get-CimInstance Win32_Process -Filter "Name='$n.exe'"
    foreach ($x in $p) {
        '   pid {0}: {1}' -f $x.ProcessId, $x.CommandLine
    }
}

'== последние 15 записей журнала планировщика по нашей задаче'
Get-WinEvent -LogName 'Microsoft-Windows-TaskScheduler/Operational' -MaxEvents 200 -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'szdiag-occt' } |
    Select-Object -First 15 |
    ForEach-Object { '   {0:HH:mm:ss} [{1}] {2}' -f $_.TimeCreated, $_.Id, ($_.Message -replace '\s+', ' ').Substring(0, [Math]::Min(150, ($_.Message -replace '\s+', ' ').Length)) }
