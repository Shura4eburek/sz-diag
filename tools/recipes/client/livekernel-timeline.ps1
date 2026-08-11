$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Таймлайн LiveKernelEvent (WER, Application Id=1001) + следы запусков игры.
#
# Грабля (СЗ 160697): жалоба «вилітає в грі», при этом APPCRASH по игре в журнале НЕТ,
# зато 284 LiveKernelEvent (0x193 VIDEO_DXGKRNL_LIVEDUMP + 0x1B0) за месяц. Секция
# livekernel в diag печатает только последние группы и честно предупреждает «без дампа —
# вывод делать нельзя» (п.94). Чтобы отличить фоновый шум драйвера от реальных зависаний
# GPU, нужны две вещи, которых в diag нет:
#   1) события по ДНЯМ и по сессиям работы (6005/6006) — шумит ли оно на простое;
#   2) когда реально запускалась игра (Prefetch + логи Gaijin), чтобы наложить одно на другое.
#
#   szcli exec <СЗ> -f tools\recipes\client\livekernel-timeline.ps1
# Параметр (szcli exec не умеет аргументы — правится здесь):
$GamePattern = 'enlisted|aces|gaijin'

'== LiveKernelEvent: все события Application 1001 c LiveKernelEvent'
$all = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1001 } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'LiveKernelEvent' })
"TOTAL: $($all.Count)"
$rows = foreach ($e in $all) {
    $code = ''
    $m = [regex]::Match($e.Message, '(?m)^\s*P1:\s*(\S+)')
    if ($m.Success) { $code = $m.Groups[1].Value }
    [pscustomobject]@{ When = $e.TimeCreated; Code = $code }
}
'-- по коду'
$rows | Group-Object Code | Sort-Object Count -Descending | ForEach-Object {
    $s = $_.Group | Sort-Object When
    "   0x{0,-6} {1,5}  {2:dd.MM HH:mm} .. {3:dd.MM HH:mm}" -f $_.Name, $_.Count, $s[0].When, $s[-1].When
}
'-- по дням (какого кода сколько)'
$rows | Group-Object { $_.When.ToString('yyyy-MM-dd') } | Sort-Object Name | ForEach-Object {
    $by = ($_.Group | Group-Object Code | Sort-Object Name | ForEach-Object { "0x$($_.Name)x$($_.Count)" }) -join ' '
    "   {0}  всего {1,4}   {2}" -f $_.Name, $_.Count, $by
}
'-- по часам за последние 3 дня работы машины'
$rows | Sort-Object When -Descending | Select-Object -First 200 |
    Group-Object { $_.When.ToString('yyyy-MM-dd HH') } | Sort-Object Name -Descending | Select-Object -First 24 |
    ForEach-Object { "   {0}:00  {1,4}" -f $_.Name, $_.Count }

'== Сессии работы машины (6005 старт журнала / 6006 штатная остановка / 6008 грязная)'
Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 6005, 6006, 6008 } -ErrorAction SilentlyContinue |
    Sort-Object TimeCreated -Descending | Select-Object -First 30 |
    ForEach-Object { "   {0:dd.MM.yyyy HH:mm:ss}  Id={1}" -f $_.TimeCreated, $_.Id }

'== Есть ли у LiveKernelEvent-отчётов настоящие дампы'
$lkr = Join-Path $env:SystemRoot 'LiveKernelReports'
if (Test-Path $lkr) {
    Get-ChildItem $lkr -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 15 |
        ForEach-Object { "   {0:dd.MM.yyyy HH:mm}  {1,8:N1} МБ  {2}" -f $_.LastWriteTime, ($_.Length / 1MB), $_.FullName }
}
else { '   C:\Windows\LiveKernelReports нет' }
$wr = Join-Path $env:ProgramData 'Microsoft\Windows\WER'
$dmp = @(Get-ChildItem $wr -Recurse -Include '*.dmp', '*.hdmp', '*.mdmp' -ErrorAction SilentlyContinue)
"   дампов в WER: $($dmp.Count)"
$dmp | Sort-Object LastWriteTime -Descending | Select-Object -First 10 |
    ForEach-Object { "   {0:dd.MM.yyyy HH:mm}  {1,8:N1} МБ  {2}" -f $_.LastWriteTime, ($_.Length / 1MB), $_.FullName }

'== Когда реально запускалась игра (Prefetch)'
$pf = @(Get-ChildItem (Join-Path $env:SystemRoot 'Prefetch') -Filter '*.pf' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match $GamePattern })
if ($pf.Count) {
    $pf | Sort-Object LastWriteTime -Descending | ForEach-Object {
        "   {0:dd.MM.yyyy HH:mm:ss}  {1}" -f $_.LastWriteTime, $_.Name
    }
}
else { '   prefetch по игре не найден (SuperFetch выключен или файлы почищены)' }

'== Файлы/логи игры на дисках'
$hits = @()
foreach ($d in (Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3').DeviceID) {
    $hits += @(Get-ChildItem "$d\" -Directory -Depth 2 -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $GamePattern })
}
foreach ($u in (Get-ChildItem (Join-Path $env:SystemDrive 'Users') -Directory -ErrorAction SilentlyContinue)) {
    foreach ($sub in @('Documents\My Games', 'AppData\Local', 'AppData\Roaming', 'Saved Games')) {
        $p = Join-Path $u.FullName $sub
        if (Test-Path $p) {
            $hits += @(Get-ChildItem $p -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $GamePattern })
        }
    }
}
if ($hits.Count) {
    $hits | Select-Object -Unique FullName, LastWriteTime | ForEach-Object {
        "   {0:dd.MM.yyyy HH:mm}  {1}" -f $_.LastWriteTime, $_.FullName
    }
    '-- свежие логи/крешлоги внутри'
    foreach ($h in ($hits | Select-Object -Unique FullName)) {
        Get-ChildItem $h.FullName -Recurse -Include '*.clog', '*.log', '*.dmp', 'crash*' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 10 |
            ForEach-Object { "   {0:dd.MM.yyyy HH:mm}  {1,7:N2} МБ  {2}" -f $_.LastWriteTime, ($_.Length / 1MB), $_.FullName }
    }
}
else { '   каталогов игры не нашли' }
