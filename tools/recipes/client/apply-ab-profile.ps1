$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Применить профиль MSI Afterburner КЛИЕНТА (разгон/лимит мощности/ручной вентилятор) —
# чтобы прогон шёл в том же режиме, в каком машина работала у клиента, а не «как приехала».
#
# Грабли, которые его породили (СЗ 160705, 12.08.2026):
#  1. Штатный CLI-ключ `MSIAfterburner.exe -Profile1` отработал БЕЗ ЭФФЕКТА: задача вернула
#     LastResult=0, а вентилятор остался в авто (0 %). Верить ключу нельзя — только сенсорам.
#  2. Сработал обход: ключи автоприменения в MSIAfterburner.cfg + перезапуск Afterburner.
#  3. `schtasks /tr` с путём, где есть пробелы ("C:\Program Files (x86)\..."), через szcli exec
#     ломается: `ERROR: Invalid argument/option - 'Files'`. Только Register-ScheduledTask,
#     где Execute и Argument — отдельные поля.
#  4. Профиль применяется в сеансе пользователя: интерактивная задача, LogonType Interactive.
#  5. ⚠️ Вентилятор слетает в авто при запуске игры / смене полноэкранного режима, а разгон и
#     PowerLimit — НЕТ. То есть «профиль применён» ≠ «вентилятор на 30 %»: проверяй по ряду,
#     что именно действует, иначе выводы о тепловом режиме будут неверными.
#
# Конфиг клиента бэкапится — вернуть перед отдачей машины (см. restore ниже).
#   szcli exec <СЗ> -f tools\recipes\client\apply-ab-profile.ps1
$Sz      = '000000'   # ← номер СЗ: попадает в имя задачи
$Restore = $false     # ← $true — вернуть конфиг клиента из бэкапа и выйти

$dir = 'C:\Program Files (x86)\MSI Afterburner'
$cfg = Join-Path $dir 'Profiles\MSIAfterburner.cfg'
$bak = Join-Path $dir 'Profiles\MSIAfterburner.cfg.szdiag-bak'
$ab  = Join-Path $dir 'MSIAfterburner.exe'
$Task = "szdiag-ab-$Sz"

if (-not (Test-Path $ab)) { throw "Afterburner не найден: $ab" }

if ($Restore) {
    if (-not (Test-Path $bak)) { throw "нет бэкапа $bak — конфиг клиента не трогали?" }
    Get-Process MSIAfterburner -ErrorAction SilentlyContinue | Stop-Process -Force
    Copy-Item $bak $cfg -Force
    Remove-Item $bak -Force
    try { Unregister-ScheduledTask -TaskName $Task -Confirm:$false -ErrorAction Stop } catch { }
    'конфиг Afterburner возвращён из бэкапа, задача снята'
    return
}

'--- профиль карты, который будет применён ---'
Get-ChildItem (Join-Path $dir 'Profiles') -Filter 'VEN_*.cfg' -ErrorAction SilentlyContinue | ForEach-Object {
    ('== ' + $_.Name)
    Get-Content $_.FullName | Where-Object { $_ -match '^(CoreClk|MemClk|PowerLimit|TempLimit|FanMode|FanSpeed)=' } |
        ForEach-Object { '   ' + $_ }
}

if (-not (Test-Path $bak)) { Copy-Item $cfg $bak -Force; ('бэкап конфига: ' + $bak) } else { ('бэкап уже есть: ' + $bak) }

$want = @{
    'ApplyOverclockingAtStartup'      = '1'
    'ApplyFanSpeedAtStartup'          = '1'
    'ApplyOverclockingAtStartupDelay' = '5'
}
$out  = New-Object System.Collections.Generic.List[string]
$seen = @{}
foreach ($l in (Get-Content $cfg)) {
    $hit = $false
    foreach ($k in $want.Keys) {
        if ($l -match ('^' + $k + '=')) { $out.Add($k + '=' + $want[$k]); $seen[$k] = $true; $hit = $true; break }
    }
    if (-not $hit) { $out.Add($l) }
}
foreach ($k in $want.Keys) {
    if (-not $seen[$k]) {
        $idx = ($out | Select-String -SimpleMatch '[Settings]' | Select-Object -First 1).LineNumber
        if ($idx) { $out.Insert($idx, $k + '=' + $want[$k]) } else { $out.Add($k + '=' + $want[$k]) }
        ('дописан ключ: ' + $k)
    } else { ('обновлён ключ: ' + $k) }
}
Set-Content -Path $cfg -Value $out -Encoding Ascii

Get-Process MSIAfterburner -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

$expl = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
if (-not $expl) { throw 'нет активного сеанса — Afterburner в сессии 0 профиль не применит' }
$o = Invoke-CimMethod -InputObject $expl -MethodName GetOwner
$user = $o.Domain + '\' + $o.User
('сеанс: ' + $user)

try { Unregister-ScheduledTask -TaskName $Task -Confirm:$false -ErrorAction Stop } catch { }
$action    = New-ScheduledTaskAction -Execute $ab
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $Task -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $Task
Start-Sleep -Seconds 45

'--- приёмка по сенсорам (в простое карта может быть в zero-fan: проверяй под нагрузкой) ---'
$csv = 'C:\OCCT\sensors.csv'
if (Test-Path $csv) {
    $rows = (@(Get-Content $csv -TotalCount 1) + @(Get-Content $csv -Tail 1)) | ConvertFrom-Csv
    $r = $rows[-1]
    foreach ($c in $r.PSObject.Properties.Name) {
        if ($c -match 'Clock\|GPU Core|Control\|GPU Fan|Fan\|GPU Fan|Power\|GPU Package|Temperature\|GPU') {
            ('   {0} = {1}' -f ($c -replace '\|/[^|]*$',''), $r.$c)
        }
    }
} else { 'ряд сенсоров не найден — запусти start-sensors.ps1 ДО применения профиля' }
('Afterburner pid: ' + ((Get-Process MSIAfterburner -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }) -join ', '))
