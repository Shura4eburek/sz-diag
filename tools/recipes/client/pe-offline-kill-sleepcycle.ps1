# Снять зависший цикл сна (szdiag-sleepcycle-*) с ОФЛАЙН-тома клиента из WinPE (СЗ 161498).
#
# Грабля: цикл «сон -> RTC-пробуждение» остался работать после теста. Задача самоперевзводится
# и усыпляет машину каждые ~90 секунд бодрствования, поэтому живой агент не успевает принять
# ни одной команды — штатный sleep-cycle-stop.ps1 до машины просто не доезжает. Из PE том
# клиента виден файлами, и цикл снимается офлайн.
#
# Что делает: ставит стоп-файл (payload при запуске сам себя удалит), удаляет XML задачи и
# её записи из TaskCache (Tree + Tasks + Plain), иначе планировщик считает задачу живой.
#
# ВЫВОД НАМЕРЕННО ЛАТИНИЦЕЙ: в WinPE агент не включает UTF-8 (PowerShellRunner, utf8=false —
# присвоение [Console]::OutputEncoding вешает powershell.exe в PE), и кириллица приезжает в
# szcli как «???».
#
# Использование: szcli exec <СЗ> -f tools\recipes\client\pe-offline-kill-sleepcycle.ps1 --timeout 180

$Sys = ''
foreach ($l in [char[]]'CDEFGHIJ') { if (Test-Path "${l}:\Windows\System32\config\SYSTEM") { $Sys = "${l}:"; break } }
if (-not $Sys) { 'Windows volume not found'; exit 1 }
"volume: $Sys"

# 1. stop-file: payload sees it on next start and deletes its own task
if (-not (Test-Path "$Sys\OCCT")) { New-Item -ItemType Directory "$Sys\OCCT" | Out-Null }
New-Item -ItemType File "$Sys\OCCT\stop-sleep-test" -Force | Out-Null
'stop-file created: OCCT\stop-sleep-test'

# 2. task xml files
$tasks = Get-ChildItem "$Sys\Windows\System32\Tasks" -Filter 'szdiag-sleepcycle-*' -ErrorAction SilentlyContinue
if (-not $tasks) { 'no sleepcycle task files' }
foreach ($t in $tasks) { Remove-Item $t.FullName -Force; "task xml removed: $($t.Name)" }

# 3. TaskCache in SOFTWARE hive (task stays alive for scheduler without this)
reg unload 'HKLM\SZSOFT' 2>$null | Out-Null
reg load 'HKLM\SZSOFT' "$Sys\Windows\System32\config\SOFTWARE" 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { 'reg load SOFTWARE failed'; exit 1 }

$tc = 'HKLM:\SZSOFT\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache'
foreach ($node in (Get-ChildItem "$tc\Tree" -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'szdiag-sleepcycle-*' })) {
    $name = $node.PSChildName
    $id = (Get-ItemProperty $node.PSPath -Name Id -ErrorAction SilentlyContinue).Id
    "TaskCache entry: $name  id=$id"
    foreach ($sub in @('Tasks', 'Plain', 'Logon', 'Boot', 'Maintenance')) {
        $p = "$tc\$sub\$id"
        if ($id -and (Test-Path $p)) { Remove-Item $p -Recurse -Force; "   removed $sub\$id" }
    }
    Remove-Item $node.PSPath -Recurse -Force
    "   removed Tree\$name"
}
reg unload 'HKLM\SZSOFT' 2>$null | Out-Null

'--- check ---'
$left = Get-ChildItem "$Sys\Windows\System32\Tasks" -Filter 'szdiag-sleepcycle-*' -ErrorAction SilentlyContinue
if ($left) { "STILL THERE: $($left.Name)" } else { 'sleepcycle task files: none' }
if (Test-Path "$Sys\OCCT\stop-sleep-test") { 'stop-file: present' } else { 'stop-file: MISSING' }
