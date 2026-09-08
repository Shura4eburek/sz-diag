# Рецепт: снять ФАКТИЧЕСКИЕ параметры памяти «как приехало» — до любых правок BIOS.
#
# Зачем: вердикт по XMP/EXPO строится на разнице «профиль против стока», а для этого
# нужно зафиксировать, на чём машина реально работала у клиента. ConfiguredClockSpeed —
# это то, что применено сейчас; Speed — то, что заявлено модулем (маркетинговая частота
# профиля). Расхождение между ними и говорит, включён профиль или нет.
#
# Грабля: Win32_PhysicalMemory.Speed на DDR5 у части плат возвращает частоту профиля даже
# при отключённом XMP — опираться только на ConfiguredClockSpeed.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

Write-Output '=== Модули памяти ==='
Get-CimInstance Win32_PhysicalMemory |
    Select-Object @{n='Слот';e={$_.DeviceLocator}},
        @{n='Банк';e={$_.BankLabel}},
        Manufacturer,
        PartNumber,
        SerialNumber,
        @{n='ГБ';e={[math]::Round($_.Capacity/1GB)}},
        @{n='Заявлено';e={$_.Speed}},
        @{n='Применено';e={$_.ConfiguredClockSpeed}},
        @{n='ВольтажmV';e={$_.ConfiguredVoltage}} |
    Format-List | Out-String -Width 200

Write-Output '=== Итого ==='
$m = Get-CimInstance Win32_PhysicalMemory
"Планок: $($m.Count), суммарно $([math]::Round(($m | Measure-Object Capacity -Sum).Sum/1GB)) ГБ"
"Применённая частота (уникальные): $((($m.ConfiguredClockSpeed) | Sort-Object -Unique) -join ', ') МТ/с"

Write-Output ''
Write-Output '=== Плата и BIOS ==='
Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer, Product, Version, SerialNumber |
    Format-List | Out-String -Width 200
Get-CimInstance Win32_BIOS |
    Select-Object Manufacturer, SMBIOSBIOSVersion, Name,
        @{n='Дата';e={$_.ConvertToDateTime($_.ReleaseDate).ToString('yyyy-MM-dd')}} |
    Format-List | Out-String -Width 200

Write-Output '=== Процессор ==='
Get-CimInstance Win32_Processor |
    Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, ProcessorId |
    Format-List | Out-String -Width 200

Write-Output '=== Видеокарта ==='
Get-CimInstance Win32_VideoController |
    Select-Object Name, DriverVersion, DriverDate, PNPDeviceID,
        @{n='ВидеопамятьГБ';e={[math]::Round($_.AdapterRAM/1GB,1)}} |
    Format-List | Out-String -Width 200

Write-Output '=== Реальная наработка: аптайм врёт из-за fast startup ==='
$os = Get-CimInstance Win32_OperatingSystem
"LastBootUpTime: $($os.LastBootUpTime)"
"Fast startup (HiberbootEnabled): " + (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
