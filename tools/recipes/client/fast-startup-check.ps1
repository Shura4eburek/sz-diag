$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Включён ли быстрый запуск Windows (Fast Startup / HiberbootEnabled) и что это значит
# для нашей детекции вырубонов.
#
# Грабля (СЗ 161346): hub считает вырубоном смену boot-time. Но при включённом быстром
# запуске выключение питания кнопкой «Завершение работы» НЕ сбрасывает LastBootUpTime —
# ядро сохраняется в гибернационный файл. То есть машину выключали и включали, а по нашей
# метрике аптайм «непрерывно растёт». Перезагрузка (Restart) быстрый запуск не использует
# и boot-time сбрасывает — отсюда расхождение, которое на живой заявке выглядело как
# «uptime врёт».
#
# Практический вывод: при включённом быстром запуске «аптайм N часов» не доказывает, что
# машина не выключалась. Опираться нужно на события Kernel-Power 41 / 6008 в журнале.

$key = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'
$hb  = (Get-ItemProperty $key -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled

if ($null -eq $hb) {
    'HiberbootEnabled: параметра нет (обычно означает, что быстрый запуск включён по умолчанию)'
} elseif ($hb -eq 1) {
    'HiberbootEnabled = 1 -> быстрый запуск ВКЛЮЧЁН'
    'ВНИМАНИЕ: выключение питания не сбрасывает boot-time, детекция вырубонов по аптайму ненадёжна.'
} else {
    'HiberbootEnabled = 0 -> быстрый запуск выключен, boot-time меняется при каждом старте'
}

$os = Get-CimInstance Win32_OperatingSystem
"LastBootUpTime: $($os.LastBootUpTime)"
"Аптайм по версии ОС: $([math]::Round(((Get-Date) - $os.LastBootUpTime).TotalHours, 2)) ч"

'=== Реальные старты и выключения из журнала (6005 старт, 6006 штатное, 6008 грязное) ==='
$ev = Get-WinEvent -FilterHashtable @{ LogName='System'; Id=6005,6006,6008; StartTime=(Get-Date).AddDays(-30) } -ErrorAction SilentlyContinue
if (-not $ev) { '   событий нет' }
else {
    $ev | Select-Object -First 20 | ForEach-Object {
        $what = switch ($_.Id) { 6005 {'старт журнала (загрузка)'} 6006 {'штатное выключение'} 6008 {'ГРЯЗНОЕ выключение'} }
        "   {0:dd.MM HH:mm:ss}  Id={1}  {2}" -f $_.TimeCreated, $_.Id, $what
    }
}

'=== Гибернация / файл быстрого запуска ==='
$hf = Get-Item 'C:\hiberfil.sys' -Force -ErrorAction SilentlyContinue
if ($hf) { "   hiberfil.sys есть, размер $([math]::Round($hf.Length/1GB,1)) ГБ (быстрый запуск/гибернация задействованы)" }
else { '   hiberfil.sys нет (гибернация и быстрый запуск отключены)' }
