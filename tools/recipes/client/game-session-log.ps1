# game-session-log.ps1 — история игровых сессий из логов лаунчеров + привязка к вырубонам.
#
# Грабля, которая его породила (СЗ 160705, 12.08.2026): восемь дней синтетики (70 мин CPU,
# 96 мин GPU, дни простоя, 6:44 CS2 в сервисе) не дали ничего, а разбор Steam-лога за минуту
# показал главное: у клиента 13 из 16 запусков CS2 (16–20.07) заканчивались вырубоном,
# медиана ~5 минут. Ключ — строка `Game process removed`: она есть только при штатном выходе,
# при hard-off её нет. То есть лог лаунчера отличает «поиграл и вышел» от «вырубилось в игре»,
# чего не даёт ни один журнал Windows.
#
# ВАЖНО: Steam пишет лог буферизованно, при hard-off хвост теряется — часть игровых сессий
# в логе не видна вовсе. Корреляция получается ЗАНИЖЕННОЙ, никогда не завышенной.
#
# Запуск: szcli exec <СЗ> -f tools\recipes\client\game-session-log.ps1
# Параметры правятся тут же, вверху (exec не передаёт аргументы).

$DaysBack = 45          # окно разбора
$MaxSessionH = 12       # потолок длины игровой сессии: при hard-off лаунчер не пишет `removed`,
                        # поэтому конец сессии неизвестен — дальше этого потолка вырубон
                        # к игре уже не приписываем

$ErrorActionPreference = 'Continue'
$cut = (Get-Date).AddDays(-$DaysBack)

function Get-SteamSessions {
    $dirs = @(
        "${env:ProgramFiles(x86)}\Steam\logs",
        "$env:ProgramFiles\Steam\logs"
    ) | Where-Object { Test-Path $_ }
    if (-not $dirs) { return @() }

    # имена игр из appmanifest — чтобы в отчёте были не голые AppID
    $names = @{}
    foreach ($d in $dirs) {
        $apps = Join-Path (Split-Path $d -Parent) 'steamapps'
        if (Test-Path $apps) {
            Get-ChildItem $apps -Filter 'appmanifest_*.acf' -ErrorAction SilentlyContinue | ForEach-Object {
                $txt = Get-Content $_.FullName -ErrorAction SilentlyContinue
                $id = ($_.BaseName -split '_')[1]
                $n = ($txt | Select-String '"name"\s+"(.+)"').Matches.Groups[1].Value
                if ($id -and $n) { $names[$id] = $n }
            }
        }
    }

    $rows = @()
    foreach ($d in $dirs) {
        Get-ChildItem $d -Filter '*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
            Select-String -Path $_.FullName -Pattern 'Game process (added|removed)' -ErrorAction SilentlyContinue |
                ForEach-Object {
                    if ($_.Line -match '^\[(.+?)\].*Game process (\w+).*AppID (\d+)') {
                        # Steam пишет '2026-08-11 13:03:34'. ParseExact, а не TryParse:
                        # в PS 5.1 [datetime]::TryParse с [ref] на нетипизированной переменной
                        # падает «Cannot find an overload», а локаль клиента (uk-UA) ломает Parse.
                        $t = $null
                        try { $t = [datetime]::ParseExact($matches[1], 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture) } catch { }
                        if ($t) {
                            $appid = $matches[3]
                            $rows += [pscustomobject]@{
                                Time   = $t
                                Event  = $matches[2]
                                AppId  = $appid
                                Game   = if ($names[$appid]) { $names[$appid] } else { "AppID $appid" }
                                Source = 'Steam'
                            }
                        }
                    }
                }
        }
    }
    $rows | Sort-Object Time -Unique
}

function Get-OtherLauncherHints {
    # Epic / Wargaming GC / Battle.net: полноценного разбора нет, печатаем хотя бы факт наличия
    # и даты последних логов — чтобы было видно, где ещё копать руками.
    $paths = @{
        'Epic'        = "$env:LOCALAPPDATA\EpicGamesLauncher\Saved\Logs"
        'Wargaming'   = "$env:APPDATA\..\Local\Wargaming.net\GameCenter\logs"
        'Battle.net'  = "$env:PROGRAMDATA\Battle.net\Logs"
    }
    foreach ($k in $paths.Keys) {
        if (Test-Path $paths[$k]) {
            $last = Get-ChildItem $paths[$k] -Recurse -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($last) { '{0,-12} логи есть, свежий: {1:dd.MM.yyyy HH:mm}  ({2})' -f $k, $last.LastWriteTime, $paths[$k] }
        }
    }
}

function Get-PowerEvents {
    # 41 = hard-off (без штатного завершения), 1074 = выключил человек/программа, 6008 = грязное
    Get-WinEvent -FilterHashtable @{LogName='System'; Id=41,1074,6008; StartTime=$cut} -ErrorAction SilentlyContinue |
        ForEach-Object {
            [pscustomobject]@{
                Time = $_.TimeCreated
                Kind = switch ($_.Id) { 41 {'hard-off (KP41)'} 1074 {'штатное (1074)'} 6008 {'грязное (6008)'} }
                Id   = $_.Id
            }
        } | Sort-Object Time
}

$sessions = @(Get-SteamSessions | Where-Object { $_.Time -gt $cut })
$power    = @(Get-PowerEvents)
$hardOff  = @($power | Where-Object { $_.Id -eq 41 })

'=== ИГРОВЫЕ СЕССИИ (Steam), окно {0} дн. ===' -f $DaysBack
if (-not $sessions) {
    'логов игровых сессий за окно нет (лаунчер не Steam, лог ротировался или игр не запускали)'
} else {
    $sessions | Format-Table @{n='Время';e={'{0:dd.MM HH:mm:ss}' -f $_.Time}}, Event, Game -AutoSize | Out-String -Width 200
}

'=== СОБЫТИЯ ПИТАНИЯ ==='
if ($power) {
    $power | Format-Table @{n='Время';e={'{0:dd.MM HH:mm:ss}' -f $_.Time}}, Kind -AutoSize | Out-String -Width 200
} else { 'событий 41/1074/6008 за окно нет' }

'=== ПРИВЯЗКА: запуск игры -> ближайший hard-off ==='
$starts = @($sessions | Where-Object { $_.Event -eq 'added' })
if (-not $starts -or -not $hardOff) {
    'сопоставлять нечего (нет запусков игр или нет вырубонов в окне)'
} else {
    # Сессия = [added .. конец], где конец — первое из: свой `removed`, следующий `added`
    # любой игры, старт + $MaxSessionH. Вырубон считается «в игре», только если попал внутрь
    # этого интервала — фиксированное окно рвало длинные сессии (на 160705 сессия 14.07 длилась
    # 7,7 часа, и вырубоны внутри неё уезжали в графу «вне игры»).
    $withCrash = 0
    $claimed = @{}
    $rows = foreach ($s in $starts) {
        $exit     = $sessions | Where-Object { $_.Event -eq 'removed' -and $_.AppId -eq $s.AppId -and $_.Time -gt $s.Time } | Select-Object -First 1
        $nextStart= $starts   | Where-Object { $_.Time -gt $s.Time } | Select-Object -First 1
        $end = $s.Time.AddHours($MaxSessionH)
        if ($exit      -and $exit.Time      -lt $end) { $end = $exit.Time }
        if ($nextStart -and $nextStart.Time -lt $end) { $end = $nextStart.Time }

        $crash = $hardOff | Where-Object { $_.Time -gt $s.Time -and $_.Time -le $end } | Select-Object -First 1
        if ($crash) { $withCrash++; $claimed[$crash.Time.Ticks] = $true }
        [pscustomobject]@{
            'Запуск'   = '{0:dd.MM HH:mm:ss}' -f $s.Time
            'Игра'     = $s.Game
            'Итог'     = if ($crash) { 'ВЫРУБОН' } elseif ($exit -and $exit.Time -le $end) { 'вышел сам' } else { 'конец не виден в логе' }
            'Когда'    = if ($crash) { '{0:dd.MM HH:mm:ss}' -f $crash.Time } elseif ($exit -and $exit.Time -le $end) { '{0:dd.MM HH:mm:ss}' -f $exit.Time } else { '—' }
            'Через'    = if ($crash) { '{0:N1} мин' -f ($crash.Time - $s.Time).TotalMinutes }
                         elseif ($exit -and $exit.Time -le $end) { '{0:N1} мин' -f ($exit.Time - $s.Time).TotalMinutes } else { '—' }
        }
    }
    $rows | Format-Table -AutoSize | Out-String -Width 200

    $mins = @($rows | Where-Object { $_.'Итог' -eq 'ВЫРУБОН' } | ForEach-Object { [double](($_.'Через' -replace ' мин','') -replace ',','.') })
    'ИТОГ: {0} из {1} игровых сессий закончились вырубоном' -f $withCrash, $starts.Count
    if ($mins.Count) {
        $sorted = $mins | Sort-Object
        $median = $sorted[[int][math]::Floor($sorted.Count/2)]
        'медиана времени до вырубона: {0:N1} мин, минимум {1:N1}, максимум {2:N1}' -f $median, ($sorted[0]), ($sorted[-1])
    }
    $orphan = @($hardOff | Where-Object { -not $claimed[$_.Time.Ticks] })
    'вырубонов, не попавших ни в одну игровую сессию: {0} из {1}' -f $orphan.Count, $hardOff.Count
    if ($orphan.Count) {
        $orphan | Select-Object -First 20 | ForEach-Object { '   {0:dd.MM HH:mm:ss}' -f $_.Time }
        'помни: Steam пишет лог буферизованно и при hard-off теряет хвост — часть игровых сессий в логе невидима,'
        'поэтому «вне игры» здесь завышено, а связь «игра → вырубон» занижена.'
    }
}

'=== ДРУГИЕ ЛАУНЧЕРЫ (разбирать руками) ==='
$hints = @(Get-OtherLauncherHints)
if ($hints) { $hints } else { 'других лаунчеров не найдено' }
