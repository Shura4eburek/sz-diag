$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Почему карта NVIDIA не уходит в idle-pstate: ключи PowerMizer в реестре + настройки профиля
# драйвера (NVCP «Управление электропитанием») + подозрительные «оптимизаторы FPS».
#
# Грабля (СЗ 161190): жалоба «кулери рандомно на максимум», а приборно карта висит в P0
# (1792/7201 МГц) при 0 % загрузки и вентилятор на 70 % при 37 °C. Обороты тут — СЛЕДСТВИЕ:
# карта считает, что работает на полную. Причина такого поведения почти всегда софтовая:
# PerfLevelSrc/PowerMizer* в реестре (их прописывают «твикеры» и гайды по разгону) или
# «Предпочтителен максимальный уровень производительности» в панели NVIDIA. Пока это не
# исключено, менять карту по симптому «шумит» нельзя.
#
#   szcli exec <СЗ> -f tools\recipes\client\gpu-powermizer.ps1

'== Ключи PowerMizer в классе видеоадаптеров'
$class = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
Get-ChildItem $class -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    if ($p.DriverDesc -notmatch 'NVIDIA') { return }
    "   [$($_.PSChildName)] $($p.DriverDesc)"
    foreach ($n in 'PerfLevelSrc', 'PowerMizerEnable', 'PowerMizerLevel', 'PowerMizerLevelAC',
        'PerfLevelSrcAC', 'DisableDynamicPstate', 'RMHdcpKeyglobZero', 'EnableMsHybrid') {
        $v = $p.$n
        if ($null -ne $v) { "      {0} = 0x{1:X} ({1})" -f $n, $v }
    }
}
'   Подсказка: PerfLevelSrc=0x2222 или PowerMizerEnable=0 => карта принудительно держится'
'   в максимальном pstate. Это НЕ заводское значение — так «оптимизируют» вручную.'

'== Служба nvlddmkm / FTS'
foreach ($k in 'HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\FTS',
    'HKLM:\SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters') {
    $p = Get-ItemProperty $k -ErrorAction SilentlyContinue
    if ($p) {
        "   $k"
        $p.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS' } |
            ForEach-Object { "      {0} = {1}" -f $_.Name, $_.Value }
    }
}

'== База профилей драйвера (NVCP): когда правили'
$drs = Join-Path $env:ProgramData 'NVIDIA Corporation\Drs'
Get-ChildItem $drs -ErrorAction SilentlyContinue | ForEach-Object {
    "   {0:dd.MM.yyyy HH:mm}  {1,8:N0} б  {2}" -f $_.LastWriteTime, $_.Length, $_.Name
}
'   (правка nvdrsdb позже даты установки драйвера = настройки панели меняли руками)'

'== Установленный драйвер и дата'
Get-CimInstance Win32_VideoController | Where-Object { $_.Name -match 'NVIDIA' } |
    Select-Object Name, DriverVersion, DriverDate, InstalledDisplayDrivers | Format-List

'== Софт-«ускорители» и фоновые потребители GPU'
$junk = 'Boosteroid|GameBooster|Razer Cortex|MSI Afterburner|Wise|Advanced SystemCare|IObit|CCleaner|' +
        'GeForce Experience|NVIDIA App|Throttle|Smart Game|WTFast|Outbyte|Driver Booster'
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -match $junk } |
    ForEach-Object { "   {0}  (установлен {1})" -f $_.DisplayName, $_.InstallDate }

'== Схема питания PCI Express / состояние Link State Power Management'
powercfg /query SCHEME_CURRENT SUB_PCIEXPRESS 2>$null | Select-String 'Индекс текущ|Current AC Power Setting|GUID параметра|Power Setting GUID' |
    ForEach-Object { "   $($_.Line.Trim())" }
