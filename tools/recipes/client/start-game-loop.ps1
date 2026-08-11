$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Запуск игрового цикла (game-loop.ps1) интерактивной задачей: игра — GUI-процесс, из сессии
# агента (или из-под SYSTEM) он не поднимется вообще. /it = «только когда пользователь вошёл»,
# поэтому пароль учётки не нужен — но и залогиненный сеанс обязателен (СЗ 161346).
# Сам рецепт доставляется на клиента через `szcli push <СЗ> recipes`.
$Sz   = '000000'   # ← номер СЗ: попадает в имя задачи, чтобы хвост был виден в inventory
$Task = "szdiag-game-$Sz"

$proc = Get-CimInstance Win32_Process -Filter "Name='SzDiag.Agent.exe'" | Select-Object -First 1
$base = Split-Path $proc.ExecutablePath -Parent
$script = Join-Path $base 'tools\recipes\game-loop.ps1'
if (-not (Test-Path $script)) { throw "нет $script — сначала szcli push <СЗ> recipes" }

# Под кем крутится рабочий стол: задача должна идти именно под ним, иначе игра не увидит сеанс
$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
if (-not $expl) { throw 'explorer.exe не найден — нет активного сеанса, игру запускать некому' }
$owner = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
$user  = $owner.Domain + '\' + $owner.User
('сеанс пользователя: ' + $user)

$cmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File "' + $script + '"'
schtasks /delete /tn $Task /f 2>$null | Out-Null
('create: ' + (schtasks /create /tn $Task /tr $cmd /sc once /st 00:00 /ru $user /it /rl highest /f 2>&1))
('run:    ' + (schtasks /run /tn $Task 2>&1))

Start-Sleep -Seconds 45
$g = Get-Process Cyberpunk2077 -ErrorAction SilentlyContinue
('игра: ' + $(if ($g) { 'запущена, pid ' + $g.Id } else { 'ещё не поднялась (первый старт долгий — проверить через 2-3 мин)' }))
$logs = Get-ChildItem 'C:\ProgramData\szdiag' -Filter 'game-loop-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($logs) { ('лог: ' + $logs.FullName); Get-Content $logs.FullName -Tail 5 | ForEach-Object { '   ' + $_ } } else { 'лог ещё не создан' }
