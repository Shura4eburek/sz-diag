$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Автоматический цикл холодных стартов: машина сама гасится, RTC-будильник её поднимает,
# boot-display-probe.ps1 на каждом заходе пишет строку «увидела ли винда монитор».
#
# Грабля (163194): дефект «при включении картинки то есть, то нет» ловится только
# КОЛИЧЕСТВОМ включений, а человек у стола столько раз кнопку не нажмёт — на прошлом
# заходе в сервисе вместо серии стартов гоняли стресс-тесты и закрыли «не подтверждено».
#
# Порядок (важно): первый цикл — на конфигурации КАК ПРИЕХАЛО, ничего в BIOS не меняя,
# иначе дефект не воспроизведётся и менять будет нечего.
#
# Перед запуском в BIOS ASUS: Advanced → APM Configuration → Power On By RTC = Enabled,
# RTC Alarm Date = 0 (каждый день), время = ближайший удобный час. Из полного выключения
# Windows-таймеры пробуждения не работают — только RTC-будильник платы.
#
# Установка (ставит себя задачей на старт, гасит машину через $IdleMinutes после загрузки):
#   szcli exec 163194 -f tools\recipes\client\cold-boot-cycle.ps1 --as-system
# Стоп цикла (машина больше не гасится сама):
#   szcli exec 163194 "New-Item -ItemType File 'C:\szdiag\stop-cycle' -Force" --as-system
#   szcli exec 163194 "schtasks /delete /tn szdiag-bootcycle /f" --as-system

$ErrorActionPreference = 'Stop'

# --- параметры (правятся здесь: param() в рецептах ломает запуск через exec) ---
$IdleMinutes = 12      # сколько машина живёт после старта, прежде чем сама выключится
$StopFlag = 'C:\szdiag\stop-cycle'
# ------------------------------------------------------------------------------

$self = $MyInvocation.MyCommand.Path
$here = if ($self) { Split-Path -Parent $self } else { '' }
$marker = if ($here) { Join-Path $here 'bootcycle.marker' } else { '' }

if (-not $marker -or -not (Test-Path $marker)) {
    # имя процесса агента отличается по сборкам (agent.exe / SzDiag.Agent.exe) — ищем по обоим
    $agent = Get-Process -ErrorAction SilentlyContinue |
             Where-Object { $_.ProcessName -in @('agent', 'SzDiag.Agent') } |
             Select-Object -First 1 -ExpandProperty Path
    if (-not $agent) { throw 'Не найден процесс agent.exe — некуда ставить цикл' }
    $dir = Join-Path (Split-Path -Parent $agent) 'tools\bootprobe'
    if (-not (Test-Path $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }

    $target = Join-Path $dir 'cold-boot-cycle.ps1'
    $utf8Bom = New-Object Text.UTF8Encoding($true)
    [IO.File]::WriteAllText($target, $MyInvocation.MyCommand.ScriptBlock.ToString(), $utf8Bom)
    Set-Content -Path (Join-Path $dir 'bootcycle.marker') -Value 'cycle' -Encoding ASCII

    $cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$target`""
    schtasks /create /tn szdiag-bootcycle /tr $cmd /sc onstart /ru SYSTEM /rl highest /f | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "schtasks вернул $LASTEXITCODE" }
    if (-not (Test-Path $target)) { throw "Цикл не записался: $target" }

    "цикл установлен: $target (гасит машину через $IdleMinutes мин после старта)"
    "стоп: создать файл $StopFlag"
    "ВНИМАНИЕ: без Power On By RTC в BIOS машина после выключения сама не поднимется"
    return
}

# --- рабочий режим ---
if (Test-Path $StopFlag) { "стоп-флаг на месте ($StopFlag) — машину не гашу"; return }

$log = Join-Path $here 'cold-boot-cycle.log'
$stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
Add-Content -Path $log -Value "$stamp старт зафиксирован, гашу через $IdleMinutes мин" -Encoding UTF8

Start-Sleep -Seconds ($IdleMinutes * 60)
if (Test-Path $StopFlag) { Add-Content -Path $log -Value "$stamp стоп-флаг появился — отмена" -Encoding UTF8; return }

Add-Content -Path $log -Value "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) shutdown /s" -Encoding UTF8
shutdown /s /t 30 /c "SzDiag: цикл холодных стартов (163194)"
