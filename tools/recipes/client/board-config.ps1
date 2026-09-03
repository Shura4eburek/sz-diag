$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Приборный снимок конфигурации перед вердиктом «профиль памяти» / «видео»:
# BIOS платы, модули памяти с ФАКТИЧЕСКОЙ частотой и напряжением (XMP/EXPO включён или нет),
# nvidia-smi (vBIOS, ширина/поколение PCIe, лимиты и текущая мощность, троттлинг),
# на какой GPU подключён каждый монитор, грязный бит тома.
#
# Грабли, которые это породили (СЗ 160697):
#  - секция diag «Память» даёт только Speed/ConfiguredClockSpeed, а нужны ещё ConfiguredVoltage
#    и парность банков: EXPO 6000 на 7800X3D — рабочая гипотеза, её надо подтверждать цифрами;
#  - монитор, воткнутый в iGPU при дискретке в слоте, объясняет и вылеты в игре, и livedump'ы
#    DXGKRNL — в diag этого не видно, там просто два адаптера в списке;
#  - у GPU важна ширина линии PCIe: x8 вместо x16 = плохой контакт/райзер, кандидат
#    на «вылеты + запах» не хуже разъёма питания.
#
#   szcli exec <СЗ> -f tools\recipes\client\board-config.ps1

'== BIOS / плата'
Get-CimInstance Win32_BIOS | Select-Object Manufacturer, SMBIOSBIOSVersion, ReleaseDate | Format-List
Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer, Product, Version | Format-List

'== Модули памяти (Speed=«заявлено SMBIOS» — НЕНАДЁЖНО, см. ниже; Configured*=фактически)'
Get-CimInstance Win32_PhysicalMemory |
    Select-Object BankLabel, DeviceLocator, @{n = 'GB'; e = { [int]($_.Capacity / 1GB) } }, Speed,
    ConfiguredClockSpeed, ConfiguredVoltage, MinVoltage, MaxVoltage, Manufacturer, PartNumber, SerialNumber |
    Format-List
$m = @(Get-CimInstance Win32_PhysicalMemory)
# Грабля (161716, бэклог п.207): вердикт строился на `ConfiguredClockSpeed -gt Speed`, а у
# Kingston FURY KF560C30-8 SMBIOS кладёт в `Speed` МАКСИМУМ из SPD (=6000), а не JEDEC-базу —
# сравнение никогда не срабатывает. Рецепт напечатал «работает на стоке» при реально включённом
# EXPO 6000 — ровно наоборот истине, а этот вывод подавался как приборное подтверждение и на
# нём строится вся дискриминация «профиль против стока» ([[159873]], [[160176]], [[161432]]).
# Надёжный вердикт требует либо чтения SPD напрямую, либо JEDEC-таблицы по поколению с учётом
# ранговости/платформы — этого пока нет. Печатаем ТОЛЬКО факт (ConfiguredClockSpeed уже выше),
# вердикт «сток/профиль» намеренно НЕ печатаем: лучше отсутствие ответа, чем уверенный неверный.
$configured = ($m | ForEach-Object { $_.ConfiguredClockSpeed } | Select-Object -Unique) -join ', '
"ВЫВОД: вердикт «сток/профиль» не печатается — Speed из SMBIOS на этой плате ненадёжен " +
    "(бэклог п.207). Факт — фактическая частота (ConfiguredClockSpeed) выше: $configured МГц."
$slots = Get-CimInstance Win32_PhysicalMemoryArray | Select-Object -First 1
if ($slots) { "Слотов на плате: $($slots.MemoryDevices), занято: $($m.Count)" }

'== nvidia-smi'
$smi = $null
foreach ($c in @((Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'), (Join-Path $env:ProgramFiles 'NVIDIA Corporation\NVSMI\nvidia-smi.exe'))) {
    if (Test-Path $c) { $smi = $c; break }
}
if ($smi) {
    & $smi --query-gpu=name,driver_version,vbios_version,pstate,pcie.link.gen.max,pcie.link.gen.current,pcie.link.width.max,pcie.link.width.current,temperature.gpu,power.draw,power.limit,power.max_limit,clocks_throttle_reasons.active,utilization.gpu --format=csv
    '-- ошибки/ретраи шины и питания (если поддерживается)'
    & $smi -q | Select-String -Pattern 'Retired|Remapped|Replay|Power Limit|Slowdown|Shutdown|GPU Link Info|Link Width|Link Gen' | ForEach-Object { "   $_" }
}
else { '   nvidia-smi не найден' }

'== Видеоадаптеры и мониторы (на каком GPU висит каждый экран)'
Get-CimInstance Win32_VideoController |
    Select-Object Name, @{n = 'Drv'; e = { $_.DriverVersion } }, @{n = 'Res'; e = { "$($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)" } },
    @{n = 'Hz'; e = { $_.CurrentRefreshRate } }, VideoProcessor, PNPDeviceID | Format-List
$vot = @{ 0 = 'Other'; 4 = 'DVI'; 5 = 'HDMI'; 6 = 'LVDS'; 9 = 'DisplayPort-ext'; 10 = 'DisplayPort-emb'; 11 = 'UDI-ext'; 15 = 'Internal'; 2147483648 = 'Internal' }
Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorConnectionParams -ErrorAction SilentlyContinue | ForEach-Object {
    $t = $_.VideoOutputTechnology
    $n = $vot[[int64]$t]
    if (-not $n) { $n = "код $t" }
    "   {0}  выход: {1}" -f $_.InstanceName, $n
}
Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction SilentlyContinue | ForEach-Object {
    $name = -join ($_.UserFriendlyName | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ })
    "   {0} = {1} (год {2})" -f $_.InstanceName, $name, $_.YearOfManufacture
}

'== Питание / схема'
powercfg /getactivescheme
'== Грязный бит тома C:'
& fsutil dirty query C:
Get-Volume -DriveLetter C | Select-Object DriveLetter, FileSystem, HealthStatus, OperationalStatus, SizeRemaining | Format-List
