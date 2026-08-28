param([int]$Days = 14)
# ВНИМАНИЕ: param обязан быть ПЕРВОЙ инструкцией файла.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
# Почему машина уходит в сон, хотя сон "выключен".
#
# Грабля (СЗ 161498): после sleep-off.ps1 (standby-timeout-ac 0) машина ВСЁ РАВНО засыпала.
# powercfg /change правит только АКТИВНУЮ схему — если её подменил вендорский софт
# (Armoury Crate / MSI Center / игровой центр) или сработала политика, наши нули не действуют.
# Плюс есть скрытые параметры: UNATTENDSLEEP (сон через 2 мин после авто-пробуждения) и
# сон, инициированный программой (Kernel-Power 42 Reason=5), таймаут простоя тут ни при чём.
#
# Использование: szcli exec <СЗ> -f tools\recipes\client\sleep-why.ps1 --timeout 240

function HexMin([string]$line) {
    if ($line -match '0x([0-9a-fA-F]+)') { [int]([Convert]::ToInt64($matches[1],16)/60) } else { -1 }
}

'=== АКТИВНАЯ СХЕМА ==='
(powercfg /getactivescheme) -join ' '

'=== ВСЕ СХЕМЫ И ИХ ТАЙМАУТ СНА (AC) ==='
$SLEEP_SUB = '238c9fa8-0aad-41ed-83f4-97be242c8f20'
$STANDBY   = '29f6c1db-86da-48c5-9fdb-f2b67b1f44da'
$UNATTEND  = '7bc4a2f9-d8fc-4469-b07b-33eb785aaca0'
$HIBER     = '9d7815a6-7ee4-497e-8888-515a05f02364'
foreach ($l in (powercfg /list)) {
    if ($l -match '([0-9a-fA-F-]{36})\s+\((.+?)\)') {
        $g = $matches[1]; $n = $matches[2]
        $star = if ($l -match '\*$') { ' <-- АКТИВНАЯ' } else { '' }
        $q  = powercfg /query $g $SLEEP_SUB $STANDBY 2>$null
        $ac = ($q | Select-String 'AC Power Setting|від мережі|от сети' | Select-Object -First 1)
        $dc = ($q | Select-String 'DC Power Setting|від батареї|от батареи' | Select-Object -First 1)
        "  $n [$g]$star"
        "     сон при простое AC: $(HexMin $ac.Line) мин / DC: $(HexMin $dc.Line) мин"
    }
}

'=== СКРЫТЫЕ ПАРАМЕТРЫ АКТИВНОЙ СХЕМЫ ==='
foreach ($p in @(@($UNATTEND,'UNATTENDSLEEP (сон после авто-пробуждения)'), @($HIBER,'HIBERNATEIDLE'))) {
    $q  = powercfg /query SCHEME_CURRENT $SLEEP_SUB $p[0] 2>$null
    $ac = ($q | Select-String 'AC Power Setting|від мережі|от сети' | Select-Object -First 1)
    if ($ac) { "  $($p[1]): $(HexMin $ac.Line) мин" } else { "  $($p[1]): нет данных" }
}

'=== ДОСТУПНЫЕ СОСТОЯНИЯ СНА (powercfg /a) ==='
powercfg /a

'=== ПОЛИТИКИ ПИТАНИЯ В РЕЕСТРЕ ==='
foreach ($k in @('HKLM:\SOFTWARE\Policies\Microsoft\Power\PowerSettings',
                 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Power')) {
    if (Test-Path $k) { "  $k :"; Get-ChildItem $k -Recurse -ErrorAction SilentlyContinue | ForEach-Object { "     $_" } }
    else { "  $k : нет" }
}
$hb = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
"  HiberbootEnabled: $hb"

'=== СОХРАНЁННЫЕ НАМИ ПРЕЖНИЕ ЗНАЧЕНИЯ (sleep-off.ps1) ==='
$save = 'C:\ProgramData\szdiag\power-before.json'
if (Test-Path $save) { "  файл от $((Get-Item $save).LastWriteTime):"; Get-Content $save }
else { '  файла нет — sleep-off.ps1 на этой машине не отрабатывал' }

'=== УХОДЫ В СОН (Kernel-Power 42) ==='
$ev42 = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=42; StartTime=(Get-Date).AddDays(-$Days)} -ErrorAction SilentlyContinue
if (-not $ev42) { '  событий нет' }
else {
    $reasons = @{0='Кнопка/крышка';2='Батарея';4='Тепловая';5='ПРОГРАММА (Application API)';7='Простой системы'}
    foreach ($e in ($ev42 | Select-Object -First 40)) {
        $x = [xml]$e.ToXml()
        $d = @{}; foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
        $r = $d['Reason']
        $rt = if ($reasons.ContainsKey([int]$r)) { $reasons[[int]$r] } else { "код $r" }
        "  {0:dd.MM HH:mm:ss}  TargetState={1} EffectiveState={2} Reason={3} ({4})" -f $e.TimeCreated, $d['TargetState'], $d['EffectiveState'], $r, $rt
    }
    "  всего за $Days дн: $($ev42.Count)"
}

'=== ПРОБУЖДЕНИЯ И ДЛИТЕЛЬНОСТЬ СНА (Power-Troubleshooter 1) ==='
$ev1 = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Power-Troubleshooter'; Id=1; StartTime=(Get-Date).AddDays(-$Days)} -ErrorAction SilentlyContinue
if (-not $ev1) { '  событий нет' }
else {
    foreach ($e in ($ev1 | Select-Object -First 30)) {
        $x = [xml]$e.ToXml()
        $d = @{}; foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
        $st = $d['SleepTime']; $wt = $d['WakeTime']
        $dur = ''
        try { $dur = ' сон ' + [math]::Round(([datetime]$wt - [datetime]$st).TotalMinutes,1) + ' мин' } catch {}
        "  проснулась {0:dd.MM HH:mm:ss}{1}; источник: {2} / {3}" -f $e.TimeCreated, $dur, $d['WakeSourceType'], $d['WakeSourceText']
    }
}

'=== КТО СЕЙЧАС ДЕРЖИТ СИСТЕМУ (powercfg /requests) ==='
powercfg /requests

'=== ПОСЛЕДНЕЕ ПРОБУЖДЕНИЕ (powercfg /lastwake) ==='
powercfg /lastwake
