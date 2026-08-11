$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Что случилось за время прогона: тест сам оборвался, упал, или машина вырубилась?
#
# Грабля (СЗ 161346): OCCT по расписанию на 60 минут завершился на 11-й — процесс исчез,
# машина при этом не перезагружалась (uptime рос), отчёт залился как «успешный». Без
# разбора это читается как «тест прошёл», хотя прошла шестая часть. Ровно тот же обрыв
# 28.07 был у магазина и его списали.
#
# Окно берётся от старта наблюдателя сенсоров, а не «за сутки»: иначе свежие события
# тонут в истории дефекта.
$Hours = 3

$since = (Get-Date).AddHours(-$Hours)
"Окно: последние $Hours ч (с {0:dd.MM HH:mm})" -f $since
''

'=== Процессы теста ==='
$names = 'OCCTCmd','OCCT','furmark','TM5','3DMarkCmd','lhmmon'
$any = $false
foreach ($n in $names) {
    $p = Get-Process $n -ErrorAction SilentlyContinue
    if ($p) { $any = $true; foreach ($i in $p) { "   {0} жив: pid={1}, CPU {2} c, RAM {3} МБ" -f $i.ProcessName, $i.Id, [int]$i.CPU, [int]($i.WorkingSet64/1MB) } }
}
if (-not $any) { '   ни одного процесса теста не осталось' }
''

'=== Аварии приложений (Application Error / AppCrash) ==='
Get-WinEvent -FilterHashtable @{ LogName='Application'; Id=1000,1001,1026; StartTime=$since } -ErrorAction SilentlyContinue |
    Select-Object -First 15 | ForEach-Object {
        "   {0:dd.MM HH:mm:ss} Id={1} {2}" -f $_.TimeCreated, $_.Id, (($_.Message -replace "`r?`n", ' ').Trim())
    }
''

'=== Питание / вырубоны (Kernel-Power 41, 6008 грязное выключение) ==='
$kp = Get-WinEvent -FilterHashtable @{ LogName='System'; Id=41,6008; StartTime=$since } -ErrorAction SilentlyContinue
if (-not $kp) { '   нет — машина не вырубалась' }
else { $kp | ForEach-Object { "   {0:dd.MM HH:mm:ss} Id={1}" -f $_.TimeCreated, $_.Id } }
''

'=== Железо: WHEA / disk / stornvme / nvlddmkm ==='
$hw = Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger','disk','stornvme','Ntfs','volmgr','nvlddmkm','Microsoft-Windows-DxgKrnl'; StartTime=$since } -ErrorAction SilentlyContinue
if (-not $hw) { '   нет' }
else {
    $hw | Select-Object -First 25 | ForEach-Object {
        "   {0:dd.MM HH:mm:ss} {1,-28} Id={2,-4} {3}" -f $_.TimeCreated, $_.ProviderName, $_.Id, (($_.Message -replace "`r?`n", ' ').Trim())
    }
}
''

'=== Терморегулирование (Kernel-Processor-Power 37/38 — троттлинг) ==='
$th = Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Processor-Power'; Id=37,38; StartTime=$since } -ErrorAction SilentlyContinue
if (-not $th) { '   нет' } else { $th | Select-Object -First 10 | ForEach-Object { "   {0:dd.MM HH:mm:ss} Id={1}" -f $_.TimeCreated, $_.Id } }
''

'=== Свежие минидампы ==='
$md = Get-ChildItem 'C:\Windows\Minidump\*.dmp' -ErrorAction SilentlyContinue | Where-Object LastWriteTime -gt $since
if (-not $md) { '   новых нет' } else { $md | ForEach-Object { "   {0:dd.MM HH:mm:ss}  {1}  {2} КБ" -f $_.LastWriteTime, $_.Name, [int]($_.Length/1KB) } }
