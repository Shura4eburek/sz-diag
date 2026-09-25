param([string]$On = '0')
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# HVCI / VBS (Memory Integrity) вкл/выкл — ради приборного захвата температур CPU.
#
# Грабля (СЗ 162003, 17.09.2026, после переустановки Windows). На чистой Win11 24H2 HVCI
# включён из коробки, ring0-драйвер lhmmon (WinRing0) под ним не поднимается: CSV пишется,
# выглядит рабочим, но `Core (Tctl/Tdie)` = 0 и VRM пусто. Ноль читается как «холодный CPU»,
# то есть данные не просто отсутствуют — они ВРУТ. На прежней (кастомной) сборке клиента
# HVCI был выключен, поэтому захват работал и разницу заметили не сразу.
#
# Третья грабля (там же, после первого ребута): HVCI сняли, а `Core (Tctl/Tdie)` всё равно 0.
# Виноват ОТДЕЛЬНЫЙ механизм — Microsoft Vulnerable Driver Blocklist
# (`CI\Config\VulnerableDriverBlocklistEnable = 1`): WinRing0 в нём, драйвер даже не
# разворачивается (службы нет, .sys рядом с lhmmon нет). Снимается тем же заходом и тем же
# парным откатом — иначе ребут ради HVCI потрачен зря.
#
# Вторая грабля там же: `Scenarios\HypervisorEnforcedCodeIntegrity\Enabled` на чистой 24H2
# уже равен 0, а HVCI при этом РАБОТАЕТ (default enablement идёт мимо этого ключа). Поэтому
# гасим VBS целиком — `hypervisorlaunchtype off` в BCD плюс EnableVirtualizationBasedSecurity=0.
# Проверять результат только по Win32_DeviceGuard.SecurityServicesRunning после ребута,
# а не по тому, что запись в реестр прошла.
#
# Это правка защиты системы: ставится ТОЛЬКО на время прогона и откатывается парным
# вызовом с On=1 ПЕРЕД отдачей машины (как весь остальной доступ — без следов).
# Прежние значения сохраняются в C:\OCCT\hvci-prev.json.
# Обе операции требуют перезагрузки.
#
#   szcli exec <СЗ> -f tools\recipes\client\hvci-set.ps1                 # выключить
#   szcli exec <СЗ> -f tools\recipes\client\hvci-set.ps1 --param On=1    # вернуть обратно

$want  = if ($On -eq '1' -or $On -eq 'true') { 1 } else { 0 }
$dgKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard'
$hvKey = Join-Path $dgKey 'Scenarios\HypervisorEnforcedCodeIntegrity'
$ciKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Config'
$save  = 'C:\OCCT\hvci-prev.json'

function Show-State {
    $dg = Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard -ErrorAction SilentlyContinue
    $run = if ($dg) { ($dg.SecurityServicesRunning -join ',') } else { '(нет данных)' }
    "SecurityServicesRunning = $run (2 = HVCI работает, 1 = Credential Guard)"
    'hypervisorlaunchtype: ' + (((bcdedit /enum '{current}' | Select-String 'hypervisorlaunchtype') -join ' ').Trim())
}

'--- было ---'
Show-State

if ($want -eq 0) {
    if (-not (Test-Path 'C:\OCCT')) { New-Item -ItemType Directory 'C:\OCCT' -Force | Out-Null }
    if (-not (Test-Path $save)) {
        # Сохраняем ровно то, что было: null = значения не существовало, его и вернём отсутствием.
        @{
            HvciEnabled = (Get-ItemProperty -Path $hvKey -Name Enabled -ErrorAction SilentlyContinue).Enabled
            Vbs         = (Get-ItemProperty -Path $dgKey -Name EnableVirtualizationBasedSecurity -ErrorAction SilentlyContinue).EnableVirtualizationBasedSecurity
            Blocklist   = (Get-ItemProperty -Path $ciKey -Name VulnerableDriverBlocklistEnable -ErrorAction SilentlyContinue).VulnerableDriverBlocklistEnable
            Hypervisor  = (((bcdedit /enum '{current}' | Select-String 'hypervisorlaunchtype') -join ' ').Trim())
        } | ConvertTo-Json | Set-Content $save -Encoding UTF8
        "прежние значения сохранены: $save"
    } else {
        "прежние значения уже сохранены раньше: $save (не перезаписываю)"
    }

    New-ItemProperty -Path $hvKey -Name Enabled -PropertyType DWord -Value 0 -Force | Out-Null
    New-ItemProperty -Path $dgKey -Name EnableVirtualizationBasedSecurity -PropertyType DWord -Value 0 -Force | Out-Null
    New-ItemProperty -Path $ciKey -Name VulnerableDriverBlocklistEnable -PropertyType DWord -Value 0 -Force | Out-Null
    'bcdedit: ' + (bcdedit /set '{current}' hypervisorlaunchtype off 2>&1)
    'HVCI/VBS и блоклист уязвимых драйверов будут ВЫКЛЮЧЕНЫ после перезагрузки. Вернуть: --param On=1 перед отдачей машины.'
}
else {
    $prev = if (Test-Path $save) { Get-Content $save -Raw | ConvertFrom-Json } else { $null }
    if (-not $prev) { 'ВНИМАНИЕ: файла с прежними значениями нет — возвращаю штатное состояние Win11 (HVCI вкл, гипервизор auto)' }

    $hvci = if ($prev -and $null -ne $prev.HvciEnabled) { [int]$prev.HvciEnabled } else { 1 }
    New-ItemProperty -Path $hvKey -Name Enabled -PropertyType DWord -Value $hvci -Force | Out-Null
    "Scenarios\HVCI\Enabled = $hvci"

    if ($prev -and $null -ne $prev.Vbs) {
        New-ItemProperty -Path $dgKey -Name EnableVirtualizationBasedSecurity -PropertyType DWord -Value ([int]$prev.Vbs) -Force | Out-Null
        "EnableVirtualizationBasedSecurity = $($prev.Vbs)"
    } else {
        Remove-ItemProperty -Path $dgKey -Name EnableVirtualizationBasedSecurity -ErrorAction SilentlyContinue
        'EnableVirtualizationBasedSecurity удалён (значения не было до нас)'
    }

    $bl = if ($prev -and $null -ne $prev.Blocklist) { [int]$prev.Blocklist } else { 1 }
    New-ItemProperty -Path $ciKey -Name VulnerableDriverBlocklistEnable -PropertyType DWord -Value $bl -Force | Out-Null
    "VulnerableDriverBlocklistEnable = $bl"

    $mode = if ($prev -and $prev.Hypervisor -match 'hypervisorlaunchtype\s+(\w+)') { $Matches[1] } else { 'Auto' }
    'bcdedit: ' + (bcdedit /set '{current}' hypervisorlaunchtype $mode 2>&1)
    if (Test-Path $save) { Remove-Item $save -Force }
    'HVCI/VBS вернутся после перезагрузки.'
}

'--- стало (в реестре; фактически — после ребута) ---'
"Scenarios\HVCI\Enabled = $((Get-ItemProperty -Path $hvKey -Name Enabled -ErrorAction SilentlyContinue).Enabled)"
"EnableVirtualizationBasedSecurity = $((Get-ItemProperty -Path $dgKey -Name EnableVirtualizationBasedSecurity -ErrorAction SilentlyContinue).EnableVirtualizationBasedSecurity)"
'Перезагрузка: szcli exec <СЗ> "Restart-Computer -Force"'
