# Параметры правь здесь.
#
# R-M12 (ревью волны 1): предупреждение «param() НЕ использовать» здесь устарело —
# `PowerShellRunner.StartsWithParamBlock` заворачивает такой скрипт в `& { ... }`, и param()
# снова оказывается первой инструкцией (см. pe-offline-events.ps1 в этом же коммите — он
# начинается с param и работает). Костыль с переменными вместо param() тут не нужен, просто
# ещё не перенесён.
$Sys  = ''   # буква тома с Windows клиента; пусто = искать самому
$Days = 30   # глубина разбора журнала
[Console]::OutputEncoding = [Text.Encoding]::UTF8
# Ограничения WinPE (нет Get-PnpDevice, Get-StorageReliabilityCounter частично пуст и т.п.) -
# сведённый список в шапке pe-offline-triage.ps1 (бэклог п.192).
#
# Разбор «почему машина уходит в сон» по ОФЛАЙН-тому клиента из WinPE (СЗ 161498).
#
# Грабля: машина засыпает через считанные минуты, окно доступа к живому агенту слишком
# короткое, чтобы успеть снять картину (два exec подряд не прошли — уснула). В PE сна нет,
# и всё нужное лежит файлами: настройки схем питания — в SYSTEM hive, история сна — в
# System.evtx. Заодно видно, действуют ли наши нули (powercfg /change правит ТОЛЬКО активную
# схему) и стоит ли скрытый UNATTENDSLEEP (сон через 2 мин после АВТОМАТИЧЕСКОГО пробуждения —
# он держит петлю «проснулась -> уснула» независимо от standby-timeout).
#
# Использование: szcli exec <СЗ> -f tools\recipes\client\pe-offline-sleep.ps1 --timeout 300
#
# #183 / б.227 (161498, 26.08): TimeCreated конвертируется в ЛОКАЛЬНУЮ таймзону PE, а не
# клиента — журнальные секции ниже печатают `.ToUniversalTime()` (UTC), помечено в заголовках.

if (-not $Sys) {
    foreach ($l in [char[]]'CDEFGHIJ') {
        if (Test-Path "${l}:\Windows\System32\config\SYSTEM") { $Sys = "${l}:"; break }
    }
}
if (-not $Sys) { 'Том с Windows клиента не найден'; exit 1 }
"=== том клиента: $Sys ==="

$SLEEP_SUB = '238c9fa8-0aad-41ed-83f4-97be242c8f20'
$settings = [ordered]@{
    '29f6c1db-86da-48c5-9fdb-f2b67b1f44da' = 'STANDBYIDLE   (сон при простое)'
    '7bc4a2f9-d8fc-4469-b07b-33eb785aaca0' = 'UNATTENDSLEEP (сон после авто-пробуждения)'
    '9d7815a6-7ee4-497e-8888-515a05f02364' = 'HIBERNATEIDLE (гибернация при простое)'
    '3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e' = 'HIBERNATEIDLE (alias)'
}

reg unload 'HKLM\SZSYS'  2>$null | Out-Null
reg unload 'HKLM\SZSOFT' 2>$null | Out-Null
$ok = $false
reg load 'HKLM\SZSYS' "$Sys\Windows\System32\config\SYSTEM" 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { $ok = $true } else { 'reg load SYSTEM не удался (том занят/грязный?)' }

if ($ok) {
    $cur = (Get-ItemProperty 'HKLM:\SZSYS\Select' -Name Current -ErrorAction SilentlyContinue).Current
    $cs  = 'HKLM:\SZSYS\ControlSet{0:D3}' -f $cur
    "ControlSet: $cs"

    $root = "$cs\Control\Power\User\PowerSchemes"
    $activeGuid = (Get-ItemProperty $root -Name ActivePowerScheme -ErrorAction SilentlyContinue).ActivePowerScheme
    "АКТИВНАЯ схема: $activeGuid"
    ''
    '=== ЗНАЧЕНИЯ ПО ВСЕМ СХЕМАМ (минуты; 0 = никогда; «нет» = наследует дефолт) ==='
    foreach ($sch in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
        $g = Split-Path $sch.Name -Leaf
        if ($g -notmatch '^[0-9a-fA-F-]{36}$') { continue }
        $star = if ($g -eq $activeGuid) { ' <-- АКТИВНАЯ' } else { '' }
        $friendly = (Get-ItemProperty $sch.PSPath -Name FriendlyName -ErrorAction SilentlyContinue).FriendlyName
        "  схема $g$star  $friendly"
        foreach ($sg in $settings.Keys) {
            $p = "$root\$g\$SLEEP_SUB\$sg"
            if (Test-Path $p) {
                $v = Get-ItemProperty $p -ErrorAction SilentlyContinue
                $ac = if ($null -ne $v.ACSettingIndex) { [int]($v.ACSettingIndex / 60) } else { 'нет' }
                $dc = if ($null -ne $v.DCSettingIndex) { [int]($v.DCSettingIndex / 60) } else { 'нет' }
                "     $($settings[$sg]): AC=$ac  DC=$dc"
            }
        }
    }

    ''
    '=== ПРОЧЕЕ ИЗ SYSTEM ==='
    $hb = (Get-ItemProperty "$cs\Control\Session Manager\Power" -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
    "  HiberbootEnabled (быстрый запуск): $hb"
    $hf = (Get-ItemProperty "$cs\Control\Power" -Name HibernateEnabled -ErrorAction SilentlyContinue).HibernateEnabled
    "  HibernateEnabled: $hf"
    $csEnabled = (Get-ItemProperty "$cs\Control\Power" -Name CsEnabled -ErrorAction SilentlyContinue).CsEnabled
    "  CsEnabled (Modern Standby / S0 low power idle): $csEnabled"
    $plt = (Get-ItemProperty "$cs\Control\Power" -Name PlatformAoAcOverride -ErrorAction SilentlyContinue).PlatformAoAcOverride
    "  PlatformAoAcOverride: $plt"

    reg unload 'HKLM\SZSYS' 2>$null | Out-Null
}

reg load 'HKLM\SZSOFT' "$Sys\Windows\System32\config\SOFTWARE" 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    ''
    '=== ПОЛИТИКИ ПИТАНИЯ (SOFTWARE hive) ==='
    $any = $false
    foreach ($k in @('HKLM:\SZSOFT\Policies\Microsoft\Power\PowerSettings','HKLM:\SZSOFT\Policies\Microsoft\Windows\Power')) {
        if (Test-Path $k) {
            $any = $true
            "  $($k -replace 'SZSOFT','SOFTWARE') :"
            Get-ChildItem $k -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
                "     $($_.Name)"
                (Get-ItemProperty $_.PSPath).PSObject.Properties |
                    Where-Object { $_.Name -notmatch '^PS' } | ForEach-Object { "        $($_.Name) = $($_.Value)" }
            }
        }
    }
    if (-not $any) { '  политик питания нет' }

    ''
    '=== ВЕНДОРСКИЙ СОФТ, КОТОРЫЙ ЛЮБИТ ПОДМЕНЯТЬ СХЕМУ ПИТАНИЯ ==='
    $pat = 'Armoury|ASUS|AI Suite|MSI Center|Dragon Center|Gaming|Razer|Corsair|iCUE|NVIDIA|Xbox|Wallpaper'
    foreach ($u in @('HKLM:\SZSOFT\Microsoft\Windows\CurrentVersion\Uninstall',
                     'HKLM:\SZSOFT\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        if (-not (Test-Path $u)) { continue }
        Get-ChildItem $u -ErrorAction SilentlyContinue | ForEach-Object {
            $d = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName
            if ($d -and $d -match $pat) { "  $d" }
        }
    }
    reg unload 'HKLM\SZSOFT' 2>$null | Out-Null
}

''
'=== ЖУРНАЛ КЛИЕНТА: СОН И ПРОБУЖДЕНИЯ ==='
$log = "$Sys\Windows\System32\winevt\Logs\System.evtx"
if (-not (Test-Path $log)) { "System.evtx не найден ($log)"; exit 0 }
$since = (Get-Date).AddDays(-$Days)
$ev = Get-WinEvent -Path $log -ErrorAction SilentlyContinue | Where-Object { $_.TimeCreated -ge $since }
"журнал: $log, записей за $Days дн: $($ev.Count)"

$reasons = @{0='Кнопка/крышка';2='Батарея';4='Тепловая';5='ПРОГРАММА (Application API)';7='Простой системы'}
''
'--- уходы в сон (Kernel-Power 42), время UTC ---'
$e42 = $ev | Where-Object { $_.Id -eq 42 -and $_.ProviderName -match 'Kernel-Power' } | Sort-Object TimeCreated
if (-not $e42) { '  событий нет' }
else {
    foreach ($e in $e42) {
        $x = [xml]$e.ToXml(); $d = @{}
        foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
        $r = 0; [void][int]::TryParse([string]$d['Reason'], [ref]$r)
        $rt = if ($reasons.ContainsKey($r)) { $reasons[$r] } else { "код $($d['Reason'])" }
        "  {0:dd.MM HH:mm:ss}  Target={1} Effective={2} Reason={3} ({4})" -f $e.TimeCreated.ToUniversalTime(), $d['TargetState'], $d['EffectiveState'], $d['Reason'], $rt
    }
    "  всего: $($e42.Count)"
}

''
'--- пробуждения (Power-Troubleshooter 1): сколько спала и что разбудило, время UTC ---'
$e1 = $ev | Where-Object { $_.Id -eq 1 -and $_.ProviderName -match 'Power-Troubleshooter' } | Sort-Object TimeCreated
if (-not $e1) { '  событий нет' }
else {
    foreach ($e in $e1) {
        $x = [xml]$e.ToXml(); $d = @{}
        foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
        $dur = ''
        try { $dur = ' спала ' + [math]::Round(([datetime]$d['WakeTime'] - [datetime]$d['SleepTime']).TotalMinutes,1) + ' мин' } catch {}
        "  проснулась {0:dd.MM HH:mm:ss}{1}; источник: {2} / {3}" -f $e.TimeCreated.ToUniversalTime(), $dur, $d['WakeSourceType'], $d['WakeSourceText']
    }
}

''
'--- вырубоны и грязные завершения рядом по времени (41 / 6008 / 1074), время UTC ---'
$ev | Where-Object { $_.Id -in 41, 6008, 1074 } | Sort-Object TimeCreated | ForEach-Object {
    '  {0:dd.MM HH:mm:ss} [{1}/{2}] {3}' -f $_.TimeCreated.ToUniversalTime(), $_.ProviderName, $_.Id, (($_.Message -replace '\s+',' ') -replace '^(.{160}).*','$1')
}
