param([int]$Days = 14)
# ВНИМАНИЕ: param обязан быть ПЕРВОЙ инструкцией файла.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
# СЗ 161498: sleep-off.ps1 (standby-timeout-ac 0) сон НЕ удержал — машина засыпает снова
# через считанные минуты, окно доступа к агенту крошечное. Поэтому порядок обратный:
# СНАЧАЛА глушим сон всеми известными способами, ПОТОМ собираем картину.
#
# Что глушим:
#  1. standby/hibernate/unattended = 0 по ВСЕМ схемам (powercfg /change правит только активную —
#     вендорский софт может подменить схему, и наши нули окажутся не в той).
#  2. UNATTENDSLEEP — скрытый параметр: после АВТОМАТИЧЕСКОГО пробуждения (не рукой юзера)
#     система через 2 мин по умолчанию уходит обратно в сон, standby-timeout тут ни при чём.
#     Самоподдерживающаяся петля: проснулась -> 2 мин -> уснула.
#  3. Держатель ES_SYSTEM_REQUIRED отдельным процессом (power request агента на 161346 не удержал),
#     задача szdiag-sleepkeeper под SYSTEM.
#
# Откат при закрытии СЗ: schtasks /delete /tn szdiag-sleepkeeper /f + вернуть значения из блока
# "БЫЛО" ниже (и/или C:\ProgramData\szdiag\power-before.json, который пишет sleep-off.ps1).
#
# Использование: szcli exec <СЗ> -f tools\recipes\client\sleep-kill.ps1 --timeout 240

$SLEEP_SUB = '238c9fa8-0aad-41ed-83f4-97be242c8f20'
$STANDBY   = '29f6c1db-86da-48c5-9fdb-f2b67b1f44da'
$UNATTEND  = '7bc4a2f9-d8fc-4469-b07b-33eb785aaca0'
$HIBER     = '9d7815a6-7ee4-497e-8888-515a05f02364'

function HexMin($line) { if ($line -match '0x([0-9a-fA-F]+)') { [int]([Convert]::ToInt64($matches[1],16)/60) } else { -1 } }
function Get-Val($scheme, $setting) {
    $q = powercfg /query $scheme $SLEEP_SUB $setting 2>$null
    $ac = ($q | Select-String 'AC Power Setting|Power Setting Index|від мережі|от сети' | Select-Object -First 1)
    if ($ac) { HexMin $ac.Line } else { -1 }
}

$schemes = @()
foreach ($l in (powercfg /list)) {
    if ($l -match '([0-9a-fA-F-]{36})\s+\((.+?)\)') {
        $schemes += [pscustomobject]@{ Guid = $matches[1]; Name = $matches[2]; Active = ($l -match '\*\s*$') }
    }
}

'=== БЫЛО (до правки) ==='
foreach ($s in $schemes) {
    $star = if ($s.Active) { ' <-- АКТИВНАЯ' } else { '' }
    "  $($s.Name) [$($s.Guid)]$star"
    "     STANDBYIDLE=$(Get-Val $s.Guid $STANDBY) мин  UNATTENDSLEEP=$(Get-Val $s.Guid $UNATTEND) мин  HIBERNATEIDLE=$(Get-Val $s.Guid $HIBER) мин"
}

foreach ($s in $schemes) {
    foreach ($set in @($STANDBY, $UNATTEND, $HIBER)) {
        powercfg /setacvalueindex $s.Guid $SLEEP_SUB $set 0 2>$null | Out-Null
        powercfg /setdcvalueindex $s.Guid $SLEEP_SUB $set 0 2>$null | Out-Null
    }
}
$active = ($schemes | Where-Object Active | Select-Object -First 1)
if ($active) { powercfg /setactive $active.Guid 2>$null | Out-Null }
powercfg /change standby-timeout-ac 0   2>$null | Out-Null
powercfg /change hibernate-timeout-ac 0 2>$null | Out-Null
powercfg /change disk-timeout-ac 0      2>$null | Out-Null

''
'=== СТАЛО (0 = никогда) ==='
foreach ($s in $schemes) {
    "  $($s.Name): STANDBYIDLE=$(Get-Val $s.Guid $STANDBY) UNATTENDSLEEP=$(Get-Val $s.Guid $UNATTEND) HIBERNATEIDLE=$(Get-Val $s.Guid $HIBER)"
}

$dir = 'C:\ProgramData\szdiag'
New-Item -ItemType Directory -Path $dir -Force -ErrorAction SilentlyContinue | Out-Null
$keeper = Join-Path $dir 'sleepkeeper.ps1'
$body = @'
$sig = '[DllImport("kernel32.dll", SetLastError=true)] public static extern uint SetThreadExecutionState(uint esFlags);'
$k = Add-Type -MemberDefinition $sig -Name Keep -Namespace Sz -PassThru
# ES_CONTINUOUS(0x80000000) | ES_SYSTEM_REQUIRED(0x1) | ES_AWAYMODE_REQUIRED(0x40)
while ($true) {
    $k::SetThreadExecutionState(0x80000041) | Out-Null
    "$(Get-Date -f 'yyyy-MM-dd HH:mm:ss') keep-alive" | Add-Content 'C:\ProgramData\szdiag\sleepkeeper.log'
    Start-Sleep -Seconds 30
}
'@
$body | Set-Content $keeper -Encoding UTF8

schtasks /delete /tn szdiag-sleepkeeper /f 2>$null | Out-Null
schtasks /create /tn szdiag-sleepkeeper /ru SYSTEM /sc onstart /rl highest /f /tr "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File $keeper" 2>$null | Out-Null
schtasks /run /tn szdiag-sleepkeeper 2>$null | Out-Null
Start-Sleep -Seconds 2
''
'=== ДЕРЖАТЕЛЬ szdiag-sleepkeeper ==='
schtasks /query /tn szdiag-sleepkeeper /fo list 2>$null | Select-String 'Status|Статус|Стан'

''
'=== ДОСТУПНЫЕ СОСТОЯНИЯ СНА ==='
powercfg /a

''
'=== КТО ДЕРЖИТ СИСТЕМУ СЕЙЧАС (powercfg /requests) ==='
powercfg /requests

''
'=== ПОСЛЕДНЕЕ ПРОБУЖДЕНИЕ ==='
powercfg /lastwake

''
'=== УХОДЫ В СОН (Kernel-Power 42) ==='
$reasons = @{0='Кнопка/крышка';2='Батарея';4='Тепловая';5='ПРОГРАММА (Application API)';7='Простой системы'}
$ev42 = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=42; StartTime=(Get-Date).AddDays(-$Days)} -ErrorAction SilentlyContinue
if (-not $ev42) { '  событий нет' }
else {
    foreach ($e in ($ev42 | Select-Object -First 40)) {
        $x = [xml]$e.ToXml(); $d = @{}
        foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
        $r = 0; [void][int]::TryParse([string]$d['Reason'], [ref]$r)
        $rt = if ($reasons.ContainsKey($r)) { $reasons[$r] } else { "код $($d['Reason'])" }
        "  {0:dd.MM HH:mm:ss}  Target={1} Effective={2} Reason={3} ({4})" -f $e.TimeCreated, $d['TargetState'], $d['EffectiveState'], $d['Reason'], $rt
    }
    "  всего за $Days дн: $($ev42.Count)"
}

''
'=== ПРОБУЖДЕНИЯ (Power-Troubleshooter 1) ==='
$ev1 = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Power-Troubleshooter'; Id=1; StartTime=(Get-Date).AddDays(-$Days)} -ErrorAction SilentlyContinue
if (-not $ev1) { '  событий нет' }
else {
    foreach ($e in ($ev1 | Select-Object -First 30)) {
        $x = [xml]$e.ToXml(); $d = @{}
        foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
        $dur = ''
        try { $dur = ' спала ' + [math]::Round(([datetime]$d['WakeTime'] - [datetime]$d['SleepTime']).TotalMinutes,1) + ' мин' } catch {}
        "  проснулась {0:dd.MM HH:mm:ss}{1}; источник: {2} / {3}" -f $e.TimeCreated, $dur, $d['WakeSourceType'], $d['WakeSourceText']
    }
}

''
'=== ПОЛИТИКИ ПИТАНИЯ В РЕЕСТРЕ ==='
foreach ($k in @('HKLM:\SOFTWARE\Policies\Microsoft\Power\PowerSettings','HKLM:\SOFTWARE\Policies\Microsoft\Windows\Power')) {
    if (Test-Path $k) { "  $k :"; Get-ChildItem $k -Recurse -ErrorAction SilentlyContinue | ForEach-Object { "     $_" } } else { "  $k : нет" }
}
$hb = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
"  HiberbootEnabled: $hb"
