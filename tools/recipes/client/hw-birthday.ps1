$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# КОГДА ЭТО ЖЕЛЕЗО ПОЯВИЛОСЬ В СИСТЕМЕ. Породила СЗ 161498: заказ — ПОЛНАЯ сборка (плата, CPU,
# ОЗУ, GPU, SSD, БЖ) от 16.06.2026, а `diag` по InstallDate решил, что ОС живёт на этой машине
# с 11.06.2025, и включил в сводку Kernel-Power 41 всю историю — вместе с событиями СТАРОГО
# компа клиента, с которого перенесли систему. Из-за этого «дефект с июня 2025» читается как
# хронический, хотя железо новое.
#
# InstallDate ОС ничему не свидетель (переживает feature update, едет внутри образа). Свидетель —
# момент, когда ЭТОТ экземпляр устройства впервые увидела ЭТА система: ключи Enum пишутся при
# первом подключении конкретного serial/instance id.
#   szcli exec <СЗ> -f tools\recipes\client\hw-birthday.ps1 --timeout 300

function Hr($t) { ''; "=== $t ===" }

Hr 'Что говорит ОС о себе'
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
"InstallDate (ОС) : {0}" -f ([DateTimeOffset]::FromUnixTimeSeconds($cv.InstallDate).LocalDateTime)
$os = Get-CimInstance Win32_OperatingSystem
"LastBoot         : {0}" -f $os.LastBootUpTime
$vol = Get-CimInstance Win32_Volume -Filter "DriveLetter='C:'" -ErrorAction SilentlyContinue
"Том C: создан    : {0}" -f (Get-Item 'C:\' -ErrorAction SilentlyContinue).CreationTime
foreach ($d in 'C:\Windows', 'C:\Users', 'C:\Windows\System32\config') {
    "  {0,-32} {1}" -f $d, (Get-Item $d -ErrorAction SilentlyContinue).CreationTime
}

# Ключевой признак: время создания подраздела Enum для КОНКРЕТНОГО экземпляра устройства.
# Через реестр .NET время не достать, поэтому берём Get-ChildItem по HKLM (PSChildName) +
# CIM-класс Win32_PnPEntity, а дату — из свойства DEVPKEY_Device_InstallDate драйвера.
Hr 'Даты установки драйверов ключевых устройств (DEVPKEY_Device_InstallDate)'
$want = @(
    @{ N = 'GPU (видеокарта)';  F = "PNPClass='Display'" }
    @{ N = 'NVMe / диски';      F = "PNPClass='DiskDrive'" }
    @{ N = 'Сетевые';           F = "PNPClass='Net'" }
    @{ N = 'Системные шины AMD';F = "PNPClass='System' AND Manufacturer LIKE '%AMD%'" }
)
foreach ($w in $want) {
    "--- $($w.N) ---"
    Get-CimInstance Win32_PnPEntity -Filter $w.F -ErrorAction SilentlyContinue | ForEach-Object {
        $inst = $null
        try {
            $inst = (Get-PnpDeviceProperty -InstanceId $_.DeviceID -KeyName 'DEVPKEY_Device_InstallDate' -ErrorAction Stop).Data
        } catch { }
        $first = $null
        try {
            $first = (Get-PnpDeviceProperty -InstanceId $_.DeviceID -KeyName 'DEVPKEY_Device_FirstInstallDate' -ErrorAction Stop).Data
        } catch { }
        "  {0,-42} install={1}  first={2}" -f ($_.Name -replace '\s+', ' '), $inst, $first
    }
}

Hr 'Начало setupapi.dev.log (когда система впервые ставила драйверы на это железо)'
$log = 'C:\Windows\INF\setupapi.dev.log'
if (Test-Path $log) {
    "размер: {0:N1} МБ, изменён {1}" -f ((Get-Item $log).Length / 1MB), (Get-Item $log).LastWriteTime
    'первые строки с датой:'
    Get-Content $log -TotalCount 400 | Where-Object { $_ -match '>>>  Section start|^\s+\d{4}/\d{2}/\d{2}' } | Select-Object -First 6 | ForEach-Object { "  $_" }
    'первое упоминание платы/чипсета/видеокарты:'
    foreach ($pat in 'B850', 'RTX 5080', 'VEN_10DE&DEV_2C02', 'SNV3S', 'KF560') {
        $hit = Select-String -Path $log -Pattern $pat -SimpleMatch -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { "  {0,-20} строка {1}" -f $pat, $hit.LineNumber } else { "  {0,-20} нет в логе" -f $pat }
    }
} else { 'setupapi.dev.log нет' }

Hr 'Профили пользователей (следы прежней машины)'
Get-CimInstance Win32_UserProfile -ErrorAction SilentlyContinue |
    Where-Object { -not $_.Special } |
    ForEach-Object { "  {0,-40} создан {1}" -f $_.LocalPath, (Get-Item $_.LocalPath -ErrorAction SilentlyContinue).CreationTime }

Hr 'Наработка диска (SMART) — сверка с возрастом сборки'
Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object {
    $s = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
    "  {0,-34} PowerOnHours={1}  Wear={2}" -f $_.FriendlyName, $s.PowerOnHours, $s.Wear
}
