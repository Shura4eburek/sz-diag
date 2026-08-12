param([int]$Days = 30)
# ВНИМАНИЕ: param обязан быть ПЕРВОЙ инструкцией файла, иначе PowerShell считает его командой
# и падает с "The term 'param' is not recognized" (наступил на это при первом прогоне на 161346).
[Console]::OutputEncoding = [Text.Encoding]::UTF8
# Таймлайн питания машины: старты, штатные и грязные выключения, уход в сон и пробуждения.
# Грабля (СЗ 161346, бэклог п.132): секция `diag система` печатает Uptime = now - LastBootUpTime,
# и из "Uptime 11 суток" был сделан вывод "машина работала 11 суток без сбоёв". На деле она
# столько не наработала (SMART Power On Hours втрое меньше). Uptime сам по себе НЕ доказывает
# работу: его не сбрасывают ни быстрый запуск, ни сон, ни гибернация. Этот рецепт разбирает,
# что машина делала на самом деле: работала, спала или стояла.
#
# Использование: szcli exec <СЗ> -f tools\recipes\client\power-timeline.ps1 --timeout 180

$since = (Get-Date).AddDays(-$Days)

$map = @{
    '12'   = 'СТАРТ ОС (Kernel-General 12)'
    '13'   = 'ОСТАНОВ ОС (Kernel-General 13)'
    '41'   = 'KP41 — питание пропало без корректного завершения'
    '42'   = 'УХОД В СОН (Kernel-Power 42)'
    '107'  = 'ПРОБУЖДЕНИЕ (Kernel-Power 107)'
    '109'  = 'Инициировано выключение ядром'
    '1'    = 'Sleep/Wake (Power-Troubleshooter 1)'
    '6005' = 'Служба журнала запущена (загрузка)'
    '6006' = 'Служба журнала остановлена (штатное выключение)'
    '6008' = 'ГРЯЗНОЕ выключение (предыдущее завершение неожиданно)'
    '6013' = 'Аптайм (ежедневная отметка)'
    '1074' = 'Выключение/перезагрузка инициированы процессом'
}

$filter = @{
    LogName      = 'System'
    ProviderName = @(
        'Microsoft-Windows-Kernel-General',
        'Microsoft-Windows-Kernel-Power',
        'Microsoft-Windows-Power-Troubleshooter',
        'EventLog',
        'User32'
    )
    Id           = @(12, 13, 41, 42, 107, 109, 1, 6005, 6006, 6008, 1074)
    StartTime    = $since
}

$ev = @(Get-WinEvent -FilterHashtable $filter -ErrorAction SilentlyContinue |
        Sort-Object TimeCreated)

if (-not $ev) { 'Событий питания за период не найдено'; exit }

"Окно: с {0:dd.MM.yyyy HH:mm} по {1:dd.MM.yyyy HH:mm}, событий: {2}" -f $since, (Get-Date), $ev.Count
"LastBootUpTime (WMI): {0:dd.MM.yyyy HH:mm:ss}" -f (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
''

$prev = $null
foreach ($e in $ev) {
    $id   = [string]$e.Id
    $name = if ($map.ContainsKey($id)) { $map[$id] } else { "Id=$id" }

    # Дыра между событиями ПИТАНИЯ. Сама по себе она НЕ значит "машина стояла":
    # работающая машина событий питания тоже не пишет. Смотреть на то, каким событием
    # дыра открывается: если предыдущее было "уход в сон" — машина спала всю дыру.
    if ($prev -and ($e.TimeCreated - $prev).TotalHours -gt 6) {
        $gap = $e.TimeCreated - $prev
        "    ...... без событий питания {0:N1} ч ({1:dd.MM HH:mm} -> {2:dd.MM HH:mm}) ......" -f `
            $gap.TotalHours, $prev, $e.TimeCreated
    }
    $prev = $e.TimeCreated

    $extra = ''
    if ($id -eq '1') {
        # Power-Troubleshooter несёт время засыпания/пробуждения в тексте события.
        # По индексам Properties брать нельзя: там сидят и не-даты, формат уезжает в мусор
        # вида "dd1017365293MM HH:mm" (проверено на 161346).
        # Порядок дат в свойствах не гарантирован — сортируем, иначе получается "проспано -282 ч".
        $times = @($e.Properties | Where-Object { $_.Value -is [datetime] } |
                   ForEach-Object { $_.Value } | Sort-Object)
        if ($times.Count -ge 2) {
            $slept = $times[1] - $times[0]
            $extra = " [сон {0:dd.MM HH:mm} -> подъём {1:dd.MM HH:mm}, проспано {2:N1} ч]" -f `
                $times[0], $times[1], $slept.TotalHours
        }
    }
    if ($id -eq '42') {
        $extra = ' ' + (($e.Message -split "`r?`n" | Where-Object { $_ -match 'Sleep|сон|Reason|Причина' }) -join '; ')
    }

    "{0:dd.MM.yyyy HH:mm:ss}  {1}{2}" -f $e.TimeCreated, $name, $extra
}
