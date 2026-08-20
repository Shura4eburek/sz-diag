$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Снимок версий MSI Center после установки: оболочка (Appx), SDK и модули — три разные сущности.
#
# Грабля (СЗ 161190): установка MSI Center 2.0.73.0 обновляет ТОЛЬКО UWP-оболочку (через DISM
# Add-ProvisionedAppxPackage). SDK и модули (Mystic Light / LEDKeeper2) остаются старых версий —
# их доставляет уже сама оболочка из своего UI. Поэтому «обновил MSI Center» ≠ «обновил
# LEDKeeper2», и проверять надо все три слоя по отдельности.
#
#   szcli exec <СЗ> -f tools\recipes\client\msi-center-versions.ps1

'== UWP-оболочка MSI Center'
Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'MSICenter|Micro-Star|MSI\.' } |
    ForEach-Object { "   {0,-40} {1}" -f $_.Name, $_.Version }
Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -match 'MSI|Micro-Star' } |
    ForEach-Object { "   provisioned: {0,-28} {1}" -f $_.DisplayName, $_.Version }

'== SDK (классическая установка)'
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -match 'MSI Center|Mystic' } |
    ForEach-Object { "   {0,-36} v{1}" -f $_.DisplayName, $_.DisplayVersion }

'== Модули: версии и даты файлов'
Get-ChildItem "${env:ProgramFiles(x86)}\MSI\MSI Center" -Recurse -File -Filter '*.exe' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 12 |
    ForEach-Object { "   {0:dd.MM.yyyy HH:mm}  v{1,-16} {2}" -f $_.LastWriteTime, $_.VersionInfo.ProductVersion, $_.Name }

'== Живые процессы MSI'
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'MSI|LEDKeeper|Mystic' } |
    ForEach-Object { "   {0,-24} pid {1,-7} старт {2:dd.MM HH:mm:ss}" -f $_.Name, $_.Id, $_.StartTime }
