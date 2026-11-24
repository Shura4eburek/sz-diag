# До якого адаптера підключені монітори — щоб пояснити GL «GDI Generic» і FurMark у P8.
# Народився на СЗ 169768: після свапу GPU FurMark-GL не стартував (GDI Generic), Vulkan крутився на 20 W.
# Варіант з Add-Type/EnumDisplayDevices через exec падав мовчки (exit 1 без stderr) — тому лише WMI/PnP.
'== Monitors (PnP) -> parent adapter'
Get-PnpDevice -Class Monitor -EA 0 | ForEach-Object {
  $parent = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Parent' -EA 0).Data
  $pname  = if ($parent) { (Get-PnpDevice -InstanceId $parent -EA 0).FriendlyName } else { '?' }
  '{0} | status={1} | parent={2}' -f $_.FriendlyName, $_.Status, $pname
}
'== WmiMonitorBasicDisplayParams (active)'
Get-CimInstance -Namespace root\wmi WmiMonitorBasicDisplayParams -EA 0 | Select-Object InstanceName,Active | Format-Table -AutoSize | Out-String -Width 200
'== Video controllers'
Get-CimInstance Win32_VideoController | Select-Object Name,Status,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate | Format-Table -AutoSize | Out-String -Width 200
