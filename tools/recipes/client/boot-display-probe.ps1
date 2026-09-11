$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Цикл холодных стартов с фиксацией «вывелось изображение или нет» (163194).
#
# Грабля, ради которой написан: жалоба «при включении картинки то есть, то нет» —
# по DP и HDMI, три разных монитора у клиента. В сервисе гоняли СТРЕСС-ТЕСТЫ и дефект
# не подтвердили: тестировали не то, дефект живёт на СТАРТЕ, а не под нагрузкой.
# Воспроизводится только серией включений, поэтому нужен автоматический счётчик стартов
# с приборной отметкой на каждом.
#
# Что пишет в CSV на каждом старте:
#   - дошла ли машина до Windows вообще (сам факт строки) и время POST→загрузка;
#   - сколько мониторов увидела винда, по какому интерфейсу (DP/HDMI) и с какого адаптера;
#   - режим адаптера (разрешение/Гц), ConfigManagerErrorCode видеокарты;
#   - ширину/скорость линка PCIe (nvidia-smi, если есть) — тренинг линка на холодную;
#   - ошибки nvlddmkm/Display/Kernel-PnP/WHEA с этого захода.
# Строка «мониторов 0» при живой винде = POST прошёл, изображения нет → дефект на
# выходах/линке. Отсутствие строки после включения = машина вообще не дошла до винды
# (тренинг памяти / POST) — это уже другая ветка, и различить их иначе нечем.
#
# Установка (один заход, скрипт ставит сам себя автостарт-задачей под SYSTEM):
#   szcli exec 163194 -f tools\recipes\client\boot-display-probe.ps1 --as-system
# Снять:
#   szcli exec 163194 "schtasks /delete /tn szdiag-bootprobe /f" --as-system
#
# ВАЖНО: чтобы получить именно ХОЛОДНЫЕ старты без человека у машины, в BIOS ASUS
# включается Advanced → APM Configuration → Power On By RTC (ежедневно/ежечасно).
# Из полного выключения Windows-таймеры пробуждения не работают — только RTC-будильник.
# Циклом выключения занимается парный рецепт cold-boot-cycle.ps1.

$ErrorActionPreference = 'Stop'
$self = $MyInvocation.MyCommand.Path
$here = if ($self) { Split-Path -Parent $self } else { '' }
$marker = if ($here) { Join-Path $here 'bootprobe.marker' } else { '' }

# ---------- режим установки: скрипт приехал через exec и рядом нет маркера ----------
if (-not $marker -or -not (Test-Path $marker)) {
    # имя процесса агента отличается по сборкам (agent.exe / SzDiag.Agent.exe) — ищем по обоим
    $agent = Get-Process -ErrorAction SilentlyContinue |
             Where-Object { $_.ProcessName -in @('agent', 'SzDiag.Agent') } |
             Select-Object -First 1 -ExpandProperty Path
    if (-not $agent) { throw 'Не найден процесс agent.exe — некуда ставить пробу (тулзы живут рядом с агентом)' }
    $dir = Join-Path (Split-Path -Parent $agent) 'tools\bootprobe'
    if (-not (Test-Path $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }

    $body = $MyInvocation.MyCommand.ScriptBlock.ToString()
    $target = Join-Path $dir 'boot-display-probe.ps1'
    $utf8Bom = New-Object Text.UTF8Encoding($true)   # без BOM PowerShell 5.1 ломает кириллицу
    [IO.File]::WriteAllText($target, $body, $utf8Bom)
    Set-Content -Path (Join-Path $dir 'bootprobe.marker') -Value 'probe' -Encoding ASCII

    $cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$target`""
    schtasks /create /tn szdiag-bootprobe /tr $cmd /sc onstart /ru SYSTEM /rl highest /f | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "schtasks вернул $LASTEXITCODE" }
    if (-not (Test-Path $target)) { throw "Проба не записалась: $target" }   # рецепт обязан проверить результат

    "проба установлена: $target"
    "задача: szdiag-bootprobe (onstart, SYSTEM)"
    "csv будет здесь: $(Join-Path $dir 'boot-display.csv')"
    "запускаю первый замер прямо сейчас"
    & $target
    return
}

# ---------- рабочий режим: замер текущего старта ----------
$csv = Join-Path $here 'boot-display.csv'
$os = Get-CimInstance Win32_OperatingSystem
$boot = $os.LastBootUpTime
$now = Get-Date

$techMap = @{ 0='VGA'; 4='DVI'; 5='HDMI'; 10='DP'; 11='DP(вбуд)'; 15='Miracast'; 2147483648='Internal' }
$ids = @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction SilentlyContinue)
$conn = @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorConnectionParams -ErrorAction SilentlyContinue)
$mons = @()
foreach ($m in $ids) {
    $name = -join ($m.UserFriendlyName | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ })
    $sn = -join ($m.SerialNumberID | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ })
    $c = $conn | Where-Object { $_.InstanceName -eq $m.InstanceName } | Select-Object -First 1
    # ключи хэштаблицы - int32: обращение по [int64] их НЕ находит и тип выхода теряется
    $t = if ($c) { $techMap[[int]$c.VideoOutputTechnology] } else { $null }
    if (-not $t -and $c) { $t = "код $($c.VideoOutputTechnology)" }
    if (-not $t) { $t = '?' }
    $mons += ('{0}/{1}/{2}' -f $name, $sn, $t)
}

$gpus = @(Get-CimInstance Win32_VideoController)
$modes = @($gpus | ForEach-Object {
    '{0} {1}x{2}@{3} err={4}' -f ($_.Name -replace 'NVIDIA GeForce ', ''),
        $_.CurrentHorizontalResolution, $_.CurrentVerticalResolution, $_.CurrentRefreshRate, $_.ConfigManagerErrorCode
})

$link = ''
$smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
if (Test-Path $smi) {
    $q = & $smi --query-gpu=pcie.link.gen.current,pcie.link.width.current,vbios_version --format=csv,noheader 2>$null
    if ($q) { $link = ($q -join ' ') -replace '\s*,\s*', '/' }
}

# события с момента этой загрузки: срыв драйвера, отвал устройства, аппаратные ошибки
$errs = @()
foreach ($pair in @(@('System', 'nvlddmkm'), @('System', 'Display'), @('System', 'Microsoft-Windows-Kernel-PnP'), @('System', 'Microsoft-Windows-WHEA-Logger'))) {
    $ev = Get-WinEvent -FilterHashtable @{ LogName = $pair[0]; ProviderName = $pair[1]; StartTime = $boot } -ErrorAction SilentlyContinue
    foreach ($e in $ev | Where-Object { $_.Level -le 3 } | Select-Object -First 5) {
        $errs += ('{0}:{1}' -f $pair[1], $e.Id)
    }
}

$row = [pscustomobject]@{
    Час        = $now.ToString('yyyy-MM-dd HH:mm:ss')
    Завантажен = $boot.ToString('yyyy-MM-dd HH:mm:ss')
    ДоВінди_с  = [int]($now - $boot).TotalSeconds
    Моніторів  = $mons.Count
    Монітори   = ($mons -join ' | ')
    Адаптери   = ($modes -join ' | ')
    PCIe_VBIOS = $link
    Події      = (($errs | Select-Object -Unique) -join ' ')
}

$exists = Test-Path $csv
$row | Export-Csv -Path $csv -Append:$exists -NoTypeInformation -Encoding UTF8
if (-not $exists) { }   # первый вызов создаёт файл с шапкой

"старт #{0}: моніторів {1} [{2}]" -f (@(Import-Csv $csv).Count), $row.Моніторів, $row.Монітори
if ($row.Моніторів -eq 0) { 'УВАГА: винда жива, а жодного монітора не бачить — це і є дефект' }
