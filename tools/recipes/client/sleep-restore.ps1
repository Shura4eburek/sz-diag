$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Парный откат к sleep-off.ps1: вернуть схему питания клиента как было.
#
# Грабля (162003, 17.09.2026): у `sleep-off.ps1` отката не было вообще — только печать
# «ОТКАТ при закрытии СЗ: powercfg /change ...» в вывод прогона. То есть откат жил в
# логе чужой сессии и терялся вместе с ней, а машина уезжала к клиенту с выключенным
# сном (модель угроз проекта: следов не оставляем).
# Значения берутся из C:\ProgramData\szdiag\power-before.json, который пишет sleep-off.
#
#   szcli exec <СЗ> -f tools\recipes\client\sleep-restore.ps1 --timeout 120

$save = 'C:\ProgramData\szdiag\power-before.json'
if (-not (Test-Path $save)) {
    "нет $save — sleep-off тут не гонялся, или файл уже убран; схему не трогаю"
    return
}

$prev = Get-Content $save -Raw | ConvertFrom-Json
$prev | Format-List | Out-String

foreach ($p in @(
    @{ Key = 'StandbyIdleMin'; Arg = 'standby-timeout-ac' },
    @{ Key = 'HibernateMin';   Arg = 'hibernate-timeout-ac' },
    @{ Key = 'DiskIdleMin';    Arg = 'disk-timeout-ac' }
)) {
    $v = $prev.($p.Key)
    if ($null -eq $v) { continue }
    $out = powercfg /change $p.Arg $v 2>&1
    "powercfg /change $($p.Arg) $v -> $(if ($out) { $out } else { 'ok' })"
}

'=== СТАЛО ==='
powercfg /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE 2>$null |
    Select-String 'AC Power Setting Index|живлення від мережі|питание от сети' | Select-Object -First 1
Remove-Item $save -Force -ErrorAction SilentlyContinue
"файл $save убран — откат сделан"
