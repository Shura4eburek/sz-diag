# СЗ 163013: ARGB-хедеры платы не управляются из SignalRGB, хотя память и видеокарта — да.
# ГРАБЛЯ: дело не в прошивке контроллера и не в правах — у SignalRGB просто НЕТ плагина
# под конкретный PID контроллера. Проверять надо до того, как лезть в перепрошивку/BIOS.
# На Gigabyte X870 GAMING X WIFI7 контроллер = HID\VID_048D&PID_57DB (usage page FF57, usage 00DB),
# а плагины SignalRGB 2.5.74 знают только 0x5702, 0x8297, 0x5711 и ждут usage 0x00CC.
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

Write-Output "=== HID-КОНТРОЛЛЕРЫ ПОДСВЕТКИ НА ПЛАТЕ (вендорские устройства) ==="
Get-PnpDevice -PresentOnly -Class HIDClass -ErrorAction SilentlyContinue |
  Where-Object { $_.FriendlyName -match 'vendor-defined' -or $_.InstanceId -match 'VID_048D|VID_0B05|VID_0DB0' } |
  ForEach-Object {
    "$($_.Status) | $($_.InstanceId)"
    (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_HardwareIds -ErrorAction SilentlyContinue).Data |
      ForEach-Object { "   hwid: $_" }
  }

Write-Output "=== КАКИЕ PID ЗНАЮТ ПЛАГИНЫ SignalRGB ==="
$plug = Get-ChildItem 'C:\Users\*\AppData\Local\VortxEngine\app-*\Signal-x64\Plugins' -Directory -ErrorAction SilentlyContinue |
  Select-Object -First 1
if (-not $plug) { Write-Output "плагины SignalRGB не найдены"; return }
Get-ChildItem $plug.FullName -Recurse -Include *.js -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -match 'Motherboard' } |
  ForEach-Object {
    $vid = (Select-String -Path $_.FullName -Pattern 'function VendorId\(\)[^\n]*').Line
    $pid_ = (Select-String -Path $_.FullName -Pattern 'function ProductId\(\)[^\n]*').Line
    $use = (Select-String -Path $_.FullName -Pattern 'endpoint\.usage ===[^\n]*').Line
    "$($_.Name)`n   $($vid.Trim())`n   $($pid_.Trim())`n   $($use.Trim())"
  }

Write-Output "=== ВЕРДИКТ ==="
Write-Output "Сверь PID из hwid устройства со списком ProductId плагинов. Нет совпадения ="
Write-Output "SignalRGB эту плату не поддерживает в принципе; ARGB рулится только родным софтом (GCC)."
