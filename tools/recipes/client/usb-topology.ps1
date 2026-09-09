# СЗ 162003: клиент говорит — мышь в красном/синем порту задней панели даёт лаги в CS,
# в чёрном лагов нет. Красный/синий = USB 3.x (xHCI), чёрный = USB 2.0 (обычно тот же xHCI,
# но другой корневой хаб/скорость). Нужна карта: какой контроллер, какой порт, какая скорость,
# какие HID сидят где, и есть ли в журнале ошибки USB/перечисления.
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Prop($id, $key) {
  try { (Get-PnpDeviceProperty -InstanceId $id -KeyName $key -ErrorAction Stop).Data } catch { $null }
}

Write-Output '=== USB-контроллеры (xHCI/EHCI) ==='
Get-PnpDevice -Class USB -Status OK -ErrorAction SilentlyContinue |
  Where-Object { $_.FriendlyName -match 'controller|контроллер|хост' } |
  ForEach-Object {
    $loc = Prop $_.InstanceId 'DEVPKEY_Device_LocationInfo'
    Write-Output ("{0}`n    id={1}`n    loc={2}" -f $_.FriendlyName, $_.InstanceId, $loc)
  }

Write-Output ''
Write-Output '=== Корневые хабы и хабы ==='
Get-PnpDevice -Class USB -ErrorAction SilentlyContinue |
  Where-Object { $_.FriendlyName -match 'hub|концентратор' } |
  ForEach-Object {
    $parent = Prop $_.InstanceId 'DEVPKEY_Device_Parent'
    Write-Output ("{0}  [{1}]`n    id={2}`n    parent={3}" -f $_.FriendlyName, $_.Status, $_.InstanceId, $parent)
  }

Write-Output ''
Write-Output '=== Все USB-устройства: порт, скорость, родитель ==='
Get-PnpDevice -Class USB, HIDClass, Mouse, Keyboard -ErrorAction SilentlyContinue |
  Sort-Object Class, FriendlyName |
  ForEach-Object {
    $id = $_.InstanceId
    $loc = Prop $id 'DEVPKEY_Device_LocationInfo'
    $parent = Prop $id 'DEVPKEY_Device_Parent'
    $addr = Prop $id 'DEVPKEY_Device_Address'
    $line = "[{0}] {1}  ({2})" -f $_.Class, $_.FriendlyName, $_.Status
    Write-Output $line
    Write-Output ("    id     = {0}" -f $id)
    if ($loc)    { Write-Output ("    port   = {0}" -f $loc) }
    if ($addr -ne $null) { Write-Output ("    addr   = {0}" -f $addr) }
    if ($parent) { Write-Output ("    parent = {0}" -f $parent) }
  }

Write-Output ''
Write-Output '=== Скорость USB-устройств (WMI) ==='
try {
  Get-CimInstance -Namespace root\wmi -ClassName MSUsb_DeviceInformation -ErrorAction Stop |
    ForEach-Object { Write-Output ("{0}" -f $_.InstanceName) }
} catch { Write-Output ("нет MSUsb_DeviceInformation: {0}" -f $_.Exception.Message) }

Write-Output ''
Write-Output '=== HID: polling / параметры в реестре ==='
$hidKeys = @(
  'HKLM:\SYSTEM\CurrentControlSet\Services\mouclass\Parameters',
  'HKLM:\SYSTEM\CurrentControlSet\Services\mouhid\Parameters',
  'HKLM:\SYSTEM\CurrentControlSet\Services\USBHUB3\Parameters',
  'HKLM:\SYSTEM\CurrentControlSet\Services\USBXHCI\Parameters',
  'HKLM:\SYSTEM\CurrentControlSet\Control\Power'
)
foreach ($k in $hidKeys) {
  if (Test-Path $k) {
    Write-Output ("--- {0}" -f $k)
    (Get-ItemProperty $k).PSObject.Properties |
      Where-Object { $_.Name -notmatch '^PS' } |
      ForEach-Object { Write-Output ("    {0} = {1}" -f $_.Name, $_.Value) }
  }
}

Write-Output ''
Write-Output '=== Selective suspend / энергосбережение на USB-узлах ==='
Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue |
  Where-Object { $_.InstanceName -match 'USB|HID' } |
  ForEach-Object { Write-Output ("{0} -> Enable={1}" -f $_.InstanceName, $_.Enable) }

Write-Output ''
Write-Output '=== События USB/PnP за 30 дней ==='
$since = (Get-Date).AddDays(-30)
$logs = @(
  'Microsoft-Windows-Kernel-PnP/Configuration',
  'Microsoft-Windows-USB-USBHUB3-Analytic',
  'Microsoft-Windows-DriverFrameworks-UserMode/Operational',
  'System'
)
foreach ($log in $logs) {
  try {
    $ev = Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = $since } -ErrorAction Stop |
      Where-Object { $_.Message -match 'USB|HID|устройств' -and $_.LevelDisplayName -match 'Ошибка|Error|Предупреждение|Warning|Критическ|Critical' }
    if ($ev) {
      Write-Output ("--- {0}: {1} записей" -f $log, $ev.Count)
      $ev | Group-Object Id, ProviderName | Sort-Object Count -Descending | Select-Object -First 10 |
        ForEach-Object { Write-Output ("    x{0}  {1}" -f $_.Count, $_.Name) }
      $ev | Sort-Object TimeCreated -Descending | Select-Object -First 15 | ForEach-Object {
        $m = ($_.Message -replace '\s+', ' ')
        if ($m.Length -gt 200) { $m = $m.Substring(0, 200) }
        Write-Output ("    {0:yyyy-MM-dd HH:mm:ss} [{1}] {2}: {3}" -f $_.TimeCreated, $_.Id, $_.ProviderName, $m)
      }
    }
  } catch { }
}

Write-Output ''
Write-Output '=== USB-устройства, которые когда-либо подключались (история) ==='
$usbstor = 'HKLM:\SYSTEM\CurrentControlSet\Enum\USB'
Get-ChildItem $usbstor -ErrorAction SilentlyContinue | ForEach-Object {
  $vidpid = $_.PSChildName
  Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
    if ($p.DeviceDesc -match 'HID|мыш|Mouse|Keyboard|клавиат|Composite|Receiver') {
      $desc = ($p.DeviceDesc -split ';')[-1]
      Write-Output ("{0}  {1}  loc={2}" -f $vidpid, $desc, $p.LocationInformation)
    }
  }
}
