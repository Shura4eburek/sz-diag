$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Пауза «ОС выключилась → ОС стартовала» по каждому перезапуску = shutdown + POST/тренировка памяти.
# Родился на СЗ 168919 (7500F + B850M, «висит на DRAM при перезапуске»): reboots/diag не показывают,
# сколько плата висела в POST между штатными перезапусками — только вырубоны. Норма ≈ 20 с;
# 60–400 с при чистом shutdown (Kernel-Boot 20 = true) → виснет прошивка (тренировка DDR5), не Windows.
# Тип берётся из 1074 за 3 мин до shutdown ('?' — инициатор не записан).
#   szcli exec <СЗ> -f tools\recipes\client\restart-post-gaps.ps1 [--param Days=30]
param([int]$Days = 30)
$since = (Get-Date).AddDays(-$Days)
$ev = Get-WinEvent -FilterHashtable @{LogName='System';StartTime=$since;ProviderName='Microsoft-Windows-Kernel-General';Id=12,13} -ea 0 | Sort-Object TimeCreated
$t1074 = Get-WinEvent -FilterHashtable @{LogName='System';StartTime=$since;Id=1074} -ea 0
for ($i = 0; $i -lt $ev.Count - 1; $i++) {
  if ($ev[$i].Id -eq 13 -and $ev[$i+1].Id -eq 12) {
    $s = $ev[$i].TimeCreated; $b = $ev[$i+1].TimeCreated
    $r = $t1074 | Where-Object { $_.TimeCreated -le $s -and $_.TimeCreated -gt $s.AddMinutes(-3) } | Select-Object -First 1
    $type = '?'
    if ($r -and $r.Message -match 'Shutdown Type:\s*(\S+)') { $type = $matches[1] }
    "{0:dd.MM HH:mm:ss} -> {1:HH:mm:ss}  gap={2,6:n0}s  type={3}" -f $s, $b, ($b - $s).TotalSeconds, $type
  }
}
"FwPOSTTime последнего старта, мс: " + (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -ea 0).FwPOSTTime
