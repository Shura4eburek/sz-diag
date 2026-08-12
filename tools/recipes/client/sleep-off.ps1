$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# Запретить машине засыпать на время диагностики + СОХРАНИТЬ прежние значения для отката.
#
# Грабля (СЗ 161346, бэклог п.137): `SleepGuard` агента (power request) сон НЕ удержал —
# машина дважды уходила в сон вечером и спала по 16-17 часов (10.08 19:32 -> 11.08 12:00,
# 11.08 19:48 -> 12.08 12:59). Мы всё это время считали, что она под наблюдением: аптайм рос,
# hub показывал онлайн. В отчёт ушло «отработала 23 ч 56 мин без вырубона», хотя реальной
# работы там 7 часов. Плюс длинный прогон, запущенный днём, к утру оказывается спящим.
#
# Откат обязателен при закрытии СЗ (значения печатаются ниже и в JSON).
# Использование: szcli exec <СЗ> -f tools\recipes\client\sleep-off.ps1 --timeout 120

$dir = 'C:\ProgramData\szdiag'
New-Item -ItemType Directory -Path $dir -Force -ErrorAction SilentlyContinue | Out-Null
$save = Join-Path $dir 'power-before.json'

function Get-TimeoutMin([string]$sub, [string]$setting) {
    # powercfg /query отдаёт текущее значение в секундах шестнадцатеричным
    $out = powercfg /query SCHEME_CURRENT $sub $setting 2>$null
    $ac = ($out | Select-String 'Current AC Power Setting Index|Поточний параметр живлення від мережі|Текущий индекс параметров электропитания \(питание от сети\)' | Select-Object -First 1)
    if ($ac -and $ac.Line -match '0x([0-9a-fA-F]+)') { [int]([Convert]::ToInt64($matches[1], 16) / 60) } else { -1 }
}

$SUB_SLEEP = 'SUB_SLEEP'
$STANDBY   = 'STANDBYIDLE'
$HIBER     = 'HIBERNATEIDLE'
$SUB_DISK  = 'SUB_DISK'
$DISKIDLE  = 'DISKIDLE'

$before = [ordered]@{
    Taken          = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    StandbyIdleMin = Get-TimeoutMin $SUB_SLEEP $STANDBY
    HibernateMin   = Get-TimeoutMin $SUB_SLEEP $HIBER
    DiskIdleMin    = Get-TimeoutMin $SUB_DISK $DISKIDLE
    Scheme         = (powercfg /getactivescheme) -join ' '
}

'=== БЫЛО ==='
"  Сон при простое (AC)      : $($before.StandbyIdleMin) мин"
"  Гибернация при простое(AC): $($before.HibernateMin) мин"
"  Отключение дисков (AC)    : $($before.DiskIdleMin) мин"
"  Схема: $($before.Scheme)"

if (-not (Test-Path $save)) {
    $before | ConvertTo-Json | Set-Content $save -Encoding UTF8
    "  прежние значения сохранены: $save"
}
else { "  файл прежних значений уже есть, не перезаписываю: $save" }

# 0 = никогда. Диски не усыпляем тоже: NVMe, ушедший в power state, ломает дисковый тест
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
powercfg /change disk-timeout-ac 0

''
'=== СТАЛО ==='
"  Сон при простое (AC)      : $(Get-TimeoutMin $SUB_SLEEP $STANDBY) мин (0 = никогда)"
"  Гибернация при простое(AC): $(Get-TimeoutMin $SUB_SLEEP $HIBER) мин"
"  Отключение дисков (AC)    : $(Get-TimeoutMin $SUB_DISK $DISKIDLE) мин"
''
'ОТКАТ при закрытии СЗ:'
"  powercfg /change standby-timeout-ac $($before.StandbyIdleMin)"
"  powercfg /change hibernate-timeout-ac $($before.HibernateMin)"
"  powercfg /change disk-timeout-ac $($before.DiskIdleMin)"
