$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
# Состояние HDR и переменной частоты на дисплее (СЗ 123456).
#
# Зачем: у DLSS-G есть документированные причины молчаливого отказа, и две из них про вывод —
# несовместимый HDR-формат свопчейна и режим представления. В системе глобально включён Auto HDR
# (DirectXUserGlobalSettings: AutoHDREnable=1), а в игре собственный HDR выключен
# (bUseHDRDisplayOutput=False) — если при этом у монитора включён HDR Windows, кадр уезжает через
# Auto HDR, и это стоит проверить прежде, чем идти к настройкам панели.
#
#   szcli exec <СЗ> -f tools\recipes\client\display-hdr-vrr-state.ps1

$ErrorActionPreference = 'SilentlyContinue'

'=== HDR по данным конфигурации дисплеев ==='
$cfg = 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Configuration'
Get-ChildItem $cfg -Recurse -Depth 2 | Where-Object { $_.PSChildName -match '^\d+$' } | ForEach-Object {
    $pp = Get-ItemProperty $_.PSPath
    if ($null -eq $pp.AdvancedColorEnabled -and $null -eq $pp.AdvancedColorSupported) { return }
    '   {0}' -f ($_.PSPath -replace '.*Configuration\\','')
    '      AdvancedColorSupported = {0}   AdvancedColorEnabled = {1}   SDRWhiteLevel = {2}' -f $pp.AdvancedColorSupported, $pp.AdvancedColorEnabled, $pp.SDRWhiteLevel
}

'=== текущий режим и глубина цвета ==='
Get-CimInstance Win32_VideoController | ForEach-Object {
    '   {0}x{1} @ {2} Гц   {3} бит' -f $_.CurrentHorizontalResolution, $_.CurrentVerticalResolution, $_.CurrentRefreshRate, $_.CurrentBitsPerPixel
}

'=== Auto HDR / оптимизации для оконных игр ==='
$g = Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\DirectX\UserGpuPreferences'
'   глобально: {0}' -f $g.DirectXUserGlobalSettings
$g.PSObject.Properties | Where-Object { $_.Name -match 'Stalker|STALKER' } | ForEach-Object { '   игра: {0} = {1}' -f $_.Name, $_.Value }

'=== игра сейчас ==='
$p = Get-Process Stalker2-Win64-Shipping
if ($p) { '   pid {0}, старт {1:HH:mm:ss}' -f $p.Id, $p.StartTime } else { '   не запущена' }
