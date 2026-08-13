$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Итог прогона y-cruncher: сколько реально отработал, дошёл ли до конца, были ли ошибки сверки.
# Породила СЗ 161716. Смысл разбора: у y-cruncher ДВА разных вердикта, и оба важны —
#   • процесс исчез, лог обрывается на середине  → машина вырубилась (hard-off);
#   • процесс жив/вышел штатно, но в логе ошибка → память врёт под нагрузкой БЕЗ вырубона
#     (так дефект проявился на 161432 — ошибки сверки при пустом WHEA).
# Поэтому «ошибок нет» засчитывается только вместе с «прогон дошёл до конца».
#   szcli exec <СЗ> -f tools\recipes\client\check-ycruncher-log.ps1
$Log = 'C:\OCCT\ycruncher.log'

if (-not (Test-Path $Log)) { "лога $Log нет — тест не запускался"; return }
$f = Get-Item $Log
"лог: {0:N0} б, последняя запись {1:dd.MM HH:mm:ss} (назад {2:hh\:mm\:ss})" -f `
    $f.Length, $f.LastWriteTime, ((Get-Date) - $f.LastWriteTime)

$kid = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like '*\ycruncher\Binaries\*' } | Select-Object -First 1
'процесс: ' + $(if ($kid) { "жив ($($kid.Name), pid=$($kid.ProcessId))" } else { 'нет — прогон завершён или машина падала' })

$txt = Get-Content $Log -Encoding UTF8
"строк в логе: $($txt.Count)"

'--- ошибки сверки / аварии ---'
# Паттерн узкий намеренно: широкий 'error|hardware' ловит шапку лога («Hardware Features:»,
# «Stop on Error: Enabled») и рисует ложную тревогу на чистом прогоне (161716).
# Настоящие вердикты y-cruncher: «Failed», «Error Detected», «Computation is incorrect».
$bad = $txt | Select-String -Pattern ':\s*Failed|Error Detected|incorrect|mismatch|corrupt' -CaseSensitive:$false
if ($bad) { $bad | Select-Object -First 20 | ForEach-Object { "  [строка $($_.LineNumber)] $($_.Line.Trim())" } }
else { '  чисто — ни одной ошибки в логе' }

# «Чисто» засчитывается только вместе с «дошёл до конца»: оборванный лог = вырубон,
# и его нельзя читать как успешный прогон.
$done   = $txt | Select-String -Pattern 'Test Finished' -Quiet
$passed = ($txt | Select-String -Pattern ':\s*Passed').Count
"итог: {0}, проверок Passed: {1}" -f $(if ($done) { 'прогон ДОШЁЛ до конца' } else { '⚠ лог ОБОРВАН — смотри szcli reboots, машина могла вырубиться' }), $passed

'--- сколько прошло итераций и тестов ---'
$iters = $txt | Select-String -Pattern '^Iteration:\s*(\d+)' | Select-Object -Last 1
if ($iters) { "  последняя: $($iters.Line.Trim())" }
$tests = $txt | Select-String -Pattern 'Running:|Testing:|Algorithm:' | Select-Object -Last 5
if ($tests) { '  последние тесты:'; $tests | ForEach-Object { "     $($_.Line.Trim())" } }

'--- хвост лога ---'
$txt | Select-Object -Last 15 | ForEach-Object { "  $_" }
