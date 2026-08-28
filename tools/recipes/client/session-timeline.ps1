$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Таймлайн сеансов машины: когда включалась, как завершалась, когда спала.
#
# Грабля, которая это породила (СЗ 161716, 26.08.2026): по KP41 казалось, что машина
# «падала и работала», а по 6008 вышло, что она валилась почти сразу после каждой загрузки,
# и главное — что после серии отказов клиент три недели её вообще не включал. Вывод
# «дефект ушёл сам» держался на интервале 00:36->14:08, посчитанном НАСКВОЗЬ, через
# штатные выключения посередине: реальная наработка оказалась вдвое меньше.
#
# Читать так:
#   6005 — старт журнала (машина поднялась);  6006 — журнал остановлен (штатное завершение);
#   1074 — кто инициировал выключение;        6008 — ПРЕДЫДУЩЕЕ завершение было грязным,
#   в тексте 6008 — реальный момент отказа (метка живости, отстаёт до нескольких минут);
#   41   — время СЛЕДУЮЩЕЙ загрузки, а не падения (память: kp41-time-is-next-boot);
#   42/107 — уход в сон и выход из него.
#
#   szcli exec <СЗ> -f toolsecipes\client\session-timeline.ps1

$Since = '2026-01-01'          # с какой даты строить таймлайн

$ev = Get-WinEvent -FilterHashtable @{LogName='System'; Id=41,42,107,1074,6005,6006,6008; StartTime=(Get-Date $Since)} -ErrorAction SilentlyContinue |
      Sort-Object TimeCreated
$os = Get-CimInstance Win32_OperatingSystem
'ОС установлена: {0:dd.MM.yyyy HH:mm}   загрузка: {1:dd.MM.yyyy HH:mm}' -f $os.InstallDate, $os.LastBootUpTime
''
$up = $null
foreach ($e in $ev) {
    $tag = switch ($e.Id) {
        6005 { 'СТАРТ  ' } 6006 { 'стоп   ' } 1074 { 'выкл   ' }
        6008 { 'ГРЯЗНОЕ' } 41 { 'KP41   ' } 42 { 'сон    ' } 107 { 'пробужд' }
    }
    $note = ''
    if ($e.Id -eq 6008) { $note = 'отказ в {0} {1}' -f $e.Properties[1].Value, $e.Properties[0].Value }
    if ($e.Id -eq 6005) { $up = $e.TimeCreated }
    if ($e.Id -eq 6006 -and $up) { $note = 'сеанс {0:hh\:mm\:ss}' -f ($e.TimeCreated - $up); $up = $null }
    '{0:MM-dd HH:mm:ss}  {1}  {2}' -f $e.TimeCreated, $tag, $note
}
''
'== суммарно'
$boots = @($ev | Where-Object Id -eq 6005)
$dirty = @($ev | Where-Object Id -eq 6008)
'   загрузок: {0}, грязных завершений: {1}, снов: {2}' -f $boots.Count, $dirty.Count, @($ev | Where-Object Id -eq 42).Count
if ($boots) { '   первая: {0:dd.MM HH:mm}, последняя: {1:dd.MM HH:mm}' -f $boots[0].TimeCreated, $boots[-1].TimeCreated }
