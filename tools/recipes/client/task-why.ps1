$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Почему запущенная задача не дала процесса. Породила СЗ 161716: Start-ScheduledTask
# отрабатывает молча, `schtasks /run` рапортует SUCCESS, а теста нет — и непонятно,
# «не стартовал» это или «стартовал и умер за секунду».
# Два реальных виновника с той заявки:
#   • LastTaskResult=0x0, процесса нет  → exe стартовал и вышел сам (у OCCT — протухшая
#     лицензия, единственный след был в stdout, который никто не читал);
#   • LastTaskResult=0x1, лога нет      → `cmd /c "exe" args > log` съел кавычки первого
#     токена. Лечится двойным уровнем: /c ""exe" args > "log"".
#   szcli exec <СЗ> -f tools\recipes\client\task-why.ps1
$Task = 'szdiag-yc-000000'   # ← имя задачи

$t = Get-ScheduledTask -TaskName $Task -ErrorAction SilentlyContinue
if (-not $t) { "задачи $Task нет"; return }
$i = Get-ScheduledTaskInfo -TaskName $Task
"State={0}  LastRunTime={1}  LastTaskResult=0x{2:X}" -f $t.State, $i.LastRunTime, $i.LastTaskResult
switch ($i.LastTaskResult) {
    0 { '  0x0 — задача отработала штатно. Нет процесса => exe вышел сам, ищи причину в его stdout' }
    1 { '  0x1 — команда не выполнилась: чаще всего кавычки в cmd /c или неверный путь' }
    2 { '  0x2 — файл не найден (проверь where-tools.ps1: тулы могли уехать в ProgramData)' }
    267011 { '  0x41303 — задача ещё ни разу не запускалась' }
    default { '  см. код в документации schtasks' }
}
'--- принципал и действие ---'
"RunAs   : $($t.Principal.UserId) / $($t.Principal.LogonType) / $($t.Principal.RunLevel)"
$t.Actions | ForEach-Object { "Execute : $($_.Execute)"; "Argument: $($_.Arguments)"; "WorkDir : $($_.WorkingDirectory)" }
'--- журнал планировщика по этой задаче ---'
Get-WinEvent -LogName 'Microsoft-Windows-TaskScheduler/Operational' -MaxEvents 200 -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match [regex]::Escape($Task) } | Select-Object -First 6 |
    ForEach-Object { "  [{0:HH:mm:ss}] Id={1} {2}" -f $_.TimeCreated, $_.Id, ($_.Message -replace '\s+', ' ' -replace '^(.{150}).*', '$1') }
