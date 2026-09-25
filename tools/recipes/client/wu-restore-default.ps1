$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Вернуть Windows Update в ШТАТНОЕ состояние, не опираясь на файл прежних значений.
#
# Грабля (162003, 17.09.2026). `szcli freeze` был вызван дважды за сессию (второй раз —
# после того, как фоновый сторож удержания пришлось снять, чтобы подменить exe CLI).
# Второй вызов перезаписал `cli\freeze\<СЗ>.json` уже ЗАМОРОЖЕННЫМ состоянием
# (`Start=4` у всех трёх служб + политика WSUS). После этого `szcli unfreeze` честно
# «вернул прежние значения» — то есть вернул заморозку, отрапортовал успех и удалил файл.
# `freeze --status` при этом печатает `Start=4` у всех служб и тут же выводит «заморозка
# не ставилась — состояние штатное»: индикатор смотрит на файл, а не на машину.
# Итог: машина уехала бы к клиенту с наглухо отключённым Windows Update.
#
# Значения ниже — дефолт Windows 11 24H2 (проверено на чистой установке этой же машины):
#   wuauserv = 3 (Manual), UsoSvc = 2 (Automatic), WaaSMedicSvc = 3 (Manual)
# Плюс снимается политика WSUS, которую ставит freeze.
#
#   szcli exec <СЗ> -f tools\recipes\client\wu-restore-default.ps1 --timeout 120
# После — обязательно сверить ФАКТ: szcli freeze --status <СЗ> (все Start != 4, WUServer пуст).

$defaults = @{ wuauserv = 3; UsoSvc = 2; WaaSMedicSvc = 3 }

foreach ($svc in $defaults.Keys) {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
    $was = (Get-ItemProperty -Path $key -Name Start -ErrorAction SilentlyContinue).Start
    Set-ItemProperty -Path $key -Name Start -Value $defaults[$svc] -Type DWord -ErrorAction SilentlyContinue
    $now = (Get-ItemProperty -Path $key -Name Start -ErrorAction SilentlyContinue).Start
    "{0,-14} Start {1} -> {2}" -f $svc, $was, $now
}

# Политика WSUS: freeze заворачивает клиента на 127.0.0.1:8530, чтобы обновления не пришли.
$au = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
foreach ($p in @(
    @{ Path = $au;            Name = 'WUServer' },
    @{ Path = $au;            Name = 'WUStatusServer' },
    @{ Path = "$au\AU";       Name = 'UseWUServer' },
    @{ Path = "$au\AU";       Name = 'NoAutoUpdate' }
)) {
    if (Test-Path $p.Path) {
        $v = (Get-ItemProperty -Path $p.Path -Name $p.Name -ErrorAction SilentlyContinue).($p.Name)
        if ($null -ne $v) {
            Remove-ItemProperty -Path $p.Path -Name $p.Name -ErrorAction SilentlyContinue
            "политика $($p.Name) = $v — снята"
        }
    }
}
# gpupdate здесь НЕ зовём: на 162003 он не уложился в 180 с и увёл exec в таймаут,
# а политика снимается самим удалением ключей — обновление GP произойдёт само.

# Службы поднимаем сразу, а не «после ребута»: машина отдаётся клиенту сегодня.
foreach ($svc in 'wuauserv', 'UsoSvc') {
    try { Start-Service $svc -ErrorAction Stop; "$svc запущена" }
    catch { "$svc не стартовала: $($_.Exception.Message)" }
}

'=== ФАКТ ==='
foreach ($svc in $defaults.Keys) {
    $s = Get-Service $svc -ErrorAction SilentlyContinue
    "{0,-14} Start={1} {2}" -f $svc,
        (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$svc" -Name Start).Start,
        $(if ($s) { $s.Status } else { '(нет службы)' })
}
"WUServer = '{0}'  NoAutoUpdate = '{1}'" -f
    (Get-ItemProperty $au -Name WUServer -ErrorAction SilentlyContinue).WUServer,
    (Get-ItemProperty "$au\AU" -Name NoAutoUpdate -ErrorAction SilentlyContinue).NoAutoUpdate
