$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Что карта делает НА ПРОСТОЕ: pstate, частоты, обороты вентилятора и температура — серией.
#
# Грабля (СЗ 161190, «кулери рандомно на максимум»): вентилятор GPU висел на 57-70% при 37 °C,
# то есть жалоба воспроизводится прямо на простое. Но прежде чем писать «дефект карты», надо
# отделить три вещи, которые в одном срезе неразличимы:
#   1) карта не уходит в idle-pstate (P0 вместо P8) — тогда высокие обороты ЗАКОННЫ, и вопрос
#      в том, что держит GPU разбуженным (оверлеи, майнер, вторая сессия, монитор/Hz);
#   2) карта в P8, но вентилятор всё равно молотит — кривая/датчик/контроллер карты;
#   3) обороты КАЧАЮТСЯ при неизменной температуре — контроллер не стабилизируется.
# Серия отвечает на все три: колонка pstate + разброс fan при постоянной temp.
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-idle-state.ps1
$Count = 30   # ← замеров
$Delay = 2    # ← секунд между замерами

$smi = $null
foreach ($c in @((Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'),
                 (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'))) {
    if (Test-Path $c) { $smi = $c; break }
}
if (-not $smi) { 'nvidia-smi не найден'; return }

'== процессы, которые держат GPU (compute + graphics)'
(& $smi --query-compute-apps=pid,process_name,used_memory --format=csv) | ForEach-Object { "   $_" }
'== серия: время | pstate | util% | ядро/память МГц | fan% | temp | вентиляторы карты'
$hdr = 'pstate,utilization.gpu,utilization.memory,clocks.current.graphics,clocks.current.memory,fan.speed,temperature.gpu,power.draw'
for ($i = 0; $i -lt $Count; $i++) {
    $l = (& $smi --query-gpu=$hdr --format=csv,noheader,nounits) -join ''
    "   {0:HH:mm:ss}  {1}" -f (Get-Date), $l
    Start-Sleep -Seconds $Delay
}
'== кто мешает уйти в idle: приложения с активным выводом'
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowTitle } |
    Select-Object -First 20 ProcessName, MainWindowTitle |
    ForEach-Object { "   {0,-22} {1}" -f $_.ProcessName, $_.MainWindowTitle }
'== режим монитора (высокий Hz / несколько экранов держат карту в P0)'
Get-CimInstance Win32_VideoController | ForEach-Object {
    "   {0}: {1}x{2} @ {3} Гц" -f $_.Name, $_.CurrentHorizontalResolution, $_.CurrentVerticalResolution, $_.CurrentRefreshRate
}
