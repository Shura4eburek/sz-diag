# СЗ 162003: короткая карта ТОЛЬКО реально подключённых сейчас USB-устройств —
# что, на каком контроллере/порту, с какой скоростью. Полный usb-topology.ps1 тонет
# в истории отключённых устройств (Status=Unknown), для «какой порт лагает» нужен срез "сейчас".
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$ctrl = @{}
Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
  Where-Object { $_.InstanceId -like 'PCI\*' -and $_.FriendlyName -match 'eXtensible Host Controller' } |
  ForEach-Object { $ctrl[$_.InstanceId.ToLower()] = $_.FriendlyName }

function Chain($id) {
  $path = @()
  $cur = $id
  for ($i = 0; $i -lt 8 -and $cur; $i++) {
    try { $p = (Get-PnpDeviceProperty -InstanceId $cur -KeyName 'DEVPKEY_Device_Parent' -ErrorAction Stop).Data }
    catch { break }
    if (-not $p) { break }
    if ($p -like 'PCI\*') {
      $n = $ctrl[$p.ToLower()]
      if (-not $n) { $n = $p }
      $path += "PCI:$n"
      break
    }
    $dev = Get-PnpDevice -InstanceId $p -ErrorAction SilentlyContinue
    $nm = if ($dev) { $dev.FriendlyName } else { $p }
    $path += $nm
    $cur = $p
  }
  ($path -join ' <- ')
}

Write-Output '=== Подключённые сейчас USB-устройства ==='
Get-PnpDevice -PresentOnly -Class USB -ErrorAction SilentlyContinue |
  Sort-Object FriendlyName | ForEach-Object {
    $id = $_.InstanceId
    $loc = try { (Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_LocationInfo' -ErrorAction Stop).Data } catch { $null }
    Write-Output ("{0}" -f $_.FriendlyName)
    Write-Output ("    id    = {0}" -f $id)
    if ($loc) { Write-Output ("    port  = {0}" -f $loc) }
    Write-Output ("    chain = {0}" -f (Chain $id))
  }

Write-Output ''
Write-Output '=== Мыши и клавиатуры (present) ==='
Get-PnpDevice -PresentOnly -Class Mouse, Keyboard, HIDClass -ErrorAction SilentlyContinue |
  Where-Object { $_.FriendlyName -match 'mouse|мыш|keyboard|клавиат' } |
  ForEach-Object {
    Write-Output ("{0}`n    id = {1}`n    chain = {2}" -f $_.FriendlyName, $_.InstanceId, (Chain $_.InstanceId))
  }

Write-Output ''
Write-Output '=== Скорости USB (root\wmi) ==='
try {
  $speeds = @{ 0 = 'Low(1.5M)'; 1 = 'Full(12M)'; 2 = 'High(480M)'; 3 = 'Super(5G)'; 4 = 'SuperPlus(10G+)' }
  Get-CimInstance -Namespace root\wmi -ClassName MSUsb_DeviceInformation -ErrorAction Stop | ForEach-Object {
    Write-Output ("{0}" -f $_.InstanceName)
  }
} catch { Write-Output ("нет: {0}" -f $_.Exception.Message) }
