$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Разбор Kernel-Power 41 по полям, а не по счётчику. Без этого «5 вырубонов» на 161312
# оказались тремя: два были кнопкой питания (PowerButtonTimestamp ≠ 0).
# Что смотреть:
#   PowerButtonTimestamp ≠ 0  → зажали кнопку, это не дефект;
#   BugcheckCode ≠ 0          → был BSOD, ищи дамп (это про софт/драйвер, не про питание);
#   BugcheckCode = 0 + WHEA пусто + дампов нет → обрыв питания / срабатывание защиты.
# Рядом печатаются 6008 (грязное выключение) и 6005/6013 — чтобы датировать простой.
#   szcli exec <СЗ> -f tools\recipes\client\kp41-detail.ps1
# 30, а не 14: на 161346 машина приехала в сервис через две недели после последнего
# вырубона, и окно в 14 дней вернуло пустоту там, где в журнале лежало 24 события.
$Days = 30

$since = (Get-Date).AddDays(-$Days)
$ev = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 41; StartTime = $since } -ErrorAction SilentlyContinue
if (-not $ev) { "Kernel-Power 41 за $Days дн.: нет"; }
else {
    "Kernel-Power 41 за $Days дн.: $($ev.Count)"
    foreach ($e in $ev) {
        $x = [xml]$e.ToXml()
        $d = @{}
        foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
        $verdict = if ($d['PowerButtonTimestamp'] -and $d['PowerButtonTimestamp'] -ne '0') { 'КНОПКА (не дефект)' }
                   elseif ($d['BugcheckCode'] -and $d['BugcheckCode'] -ne '0') { "BSOD 0x$([Convert]::ToString([int]$d['BugcheckCode'],16))" }
                   else { 'hard-off (питание/защита)' }
        "   {0:dd.MM HH:mm:ss}  {1,-26} bugcheck={2} btn={3} sleep={4}" -f `
            $e.TimeCreated, $verdict, $d['BugcheckCode'], $d['PowerButtonTimestamp'], $d['SleepInProgress']
    }
}

'Загрузки/выключения (6005 старт журнала, 6006 штатное, 6008 грязное, 6013 uptime):'
Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 6005, 6006, 6008; StartTime = $since } -ErrorAction SilentlyContinue |
    Select-Object -First 20 | ForEach-Object { "   {0:dd.MM HH:mm:ss}  Id={1}" -f $_.TimeCreated, $_.Id }

'WHEA за тот же период (пусто = не MCE):'
$w = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WHEA-Logger'; StartTime = $since } -ErrorAction SilentlyContinue
if ($w) { $w | Group-Object Id | ForEach-Object { "   Id=$($_.Name): $($_.Count)" } } else { '   нет' }
