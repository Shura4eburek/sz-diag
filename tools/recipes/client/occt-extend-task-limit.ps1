$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# ПРОДЛИТЬ ЛИМИТ ЗАДАЧИ ЖИВОМУ ПРОГОНУ OCCT, не убивая процесс.
#
# Грабля (СЗ 162003, 15.09.2026): расписание было на 3 ч, лимит задачи — 4 ч, но OCCT
# молотит ДОЛЬШЕ расписания (3 ч 40 мин при `Duration=03:00:00`, IsInfinite=False).
# Когда планировщик дойдёт до лимита, он убьёт OCCTCmd/OCCTEnterprise — и отчёт,
# который пишется ТОЛЬКО при штатном выходе, снова не сохранится. Ровно так уже
# потеряли прогон 11.09.
#
# `Set-ScheduledTask` меняет настройки задачи, НЕ трогая запущенный экземпляр:
# процесс продолжает работать, новый лимит применяется к нему же.
#
#   szcli exec <СЗ> -f tools\recipes\client\occt-extend-task-limit.ps1 --param Sz=162003 --param LimitHours=8

$Sz         = '162003'   # ← номер СЗ
$Suffix     = 'mem'      # ← szdiag-occt<Suffix>-<СЗ>
$LimitHours = 8          # ← новый лимит задачи, ч

$task = "szdiag-occt$Suffix-$Sz"
$t = Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
if (-not $t) { "задачи $task нет"; return }

$was = $t.Settings.ExecutionTimeLimit
$t.Settings.ExecutionTimeLimit = [Xml.XmlConvert]::ToString([TimeSpan]::FromHours($LimitHours))
Set-ScheduledTask -InputObject $t | Out-Null

$now = Get-ScheduledTask -TaskName $task
'лимит задачи {0}: было {1} -> стало {2}' -f $task, $was, $now.Settings.ExecutionTimeLimit

$info = Get-ScheduledTaskInfo -TaskName $task
'запущена {0:HH:mm:ss}, LastTaskResult=0x{1:X} (0x41301 = выполняется)' -f $info.LastRunTime, $info.LastTaskResult

foreach ($n in 'OCCTCmd', 'OCCTEnterprise') {
    $p = Get-Process $n -ErrorAction SilentlyContinue
    if ($p) {
        'процесс {0}: pid {1}, старт {2:HH:mm:ss}, в работе {3:hh\:mm}, CPU {4:N0} c, RAM {5:N0} МБ' -f `
            $n, $p.Id, $p.StartTime, ((Get-Date) - $p.StartTime), $p.CPU, ($p.WorkingSet64 / 1MB)
    }
}
