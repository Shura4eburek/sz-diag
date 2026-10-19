# Базовый срез ПОСЛЕ переустановки Windows, когда переустановка — разделительный тест.
# Грабля (СЗ 162003): два BSOD пришли на встроенных USB-устройствах платы (UsbHub3.sys,
# RtUsbA64.sys), и после переустановки надо знать ДО прогона — вернулись ли те же самые
# драйверы/устройства, или система теперь голая. Иначе «отходило чисто» нечем объяснить:
# то ли плата ни при чём, то ли просто нет драйвера, который падал.
# Что печатает: сторонние (не микрософтовские) драйверы ядра, USB-устройства платы
# и их службы, автозапуск, установленный софт вендора (MSI/RGB), ring0-драйверы.

$ErrorActionPreference = 'SilentlyContinue'

Write-Output '=== Storonnie drayvery yadra (ne Microsoft) ==='
Get-CimInstance Win32_SystemDriver | Where-Object { $_.State -eq 'Running' } | ForEach-Object {
    $p = $_.PathName -replace '\\\?\?\\', ''
    if ($p -and (Test-Path $p)) {
        $v = (Get-Item $p).VersionInfo
        if ($v.CompanyName -and $v.CompanyName -notmatch 'Microsoft') {
            '{0,-22} {1,-34} {2,-14} {3}' -f $_.Name, $v.CompanyName, $v.FileVersion, (Get-Item $p).LastWriteTime.ToString('yyyy-MM-dd')
        }
    }
}

Write-Output ''
Write-Output '=== USB-ustroystva platy (VID MSI 1462 / vstroennye) i ih sluzhby ==='
Get-PnpDevice -Class USB, HIDClass, MEDIA, AudioEndpoint -PresentOnly | ForEach-Object {
    $svc = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Service').Data
    '{0,-8} {1,-12} {2,-58} {3}' -f $_.Status, $svc, $_.InstanceId, $_.FriendlyName
} | Sort-Object

Write-Output ''
Write-Output '=== Drayvery Realtek USB Audio / HID (imenno oni padali na 162003) ==='
foreach ($n in 'RtUsbA64.sys', 'UsbHub3.sys', 'HidUsb.sys', 'usbccgp.sys') {
    $f = Join-Path $env:SystemRoot "System32\drivers\$n"
    if (Test-Path $f) {
        $v = (Get-Item $f).VersionInfo
        '{0,-14} {1,-16} {2,-30} {3}' -f $n, $v.FileVersion, $v.CompanyName, (Get-Item $f).LastWriteTime.ToString('yyyy-MM-dd')
    } else {
        '{0,-14} NET FAYLA' -f $n
    }
}

Write-Output ''
Write-Output '=== Ustanovlennyy soft vendora / RGB / monitoring ==='
$keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
Get-ItemProperty $keys | Where-Object { $_.DisplayName } |
    Select-Object DisplayName, DisplayVersion, Publisher, InstallDate |
    Sort-Object DisplayName | Format-Table -AutoSize | Out-String -Width 160

Write-Output ''
Write-Output '=== Avtozapusk ==='
Get-CimInstance Win32_StartupCommand | Select-Object Name, Command, Location | Format-Table -AutoSize | Out-String -Width 200

Write-Output ''
Write-Output '=== Sluzhby ne ot Microsoft (Running) ==='
Get-CimInstance Win32_Service | Where-Object { $_.State -eq 'Running' -and $_.PathName -notmatch 'Windows\\system32' } |
    Select-Object Name, DisplayName | Format-Table -AutoSize | Out-String -Width 160
