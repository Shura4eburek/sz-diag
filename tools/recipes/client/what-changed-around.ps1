# Рецепт: что менялось на машине в окне дат (драйверы из setupapi.dev.log, Windows Update, установки MSI).
# Грабля: клиент называет дату начала проблем («с 4 мая»), а diag не умеет ответить «что ставили в эти дни».
param([datetime]$From = '2026-04-25', [datetime]$To = '2026-05-12')
$ErrorActionPreference = 'SilentlyContinue'
"=== Windows Update history $($From.ToShortDateString())..$($To.ToShortDateString()) ==="
$s = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher()
$s.QueryHistory(0, $s.GetTotalHistoryCount()) | ? { $_.Date -ge $From -and $_.Date -le $To } |
  sort Date | % { "$($_.Date.ToString('MM-dd HH:mm')) rc=$($_.ResultCode) $($_.Title.Substring(0,[Math]::Min(100,$_.Title.Length)))" }
"=== setupapi.dev.log: установки драйверов дисплея/чипсета в окне ==="
$log = Get-Content C:\Windows\INF\setupapi.dev.log
$blk = $null
foreach ($l in $log) {
  if ($l -match '^>>>\s+\[(.*)\]') { $blk = $l; continue }
  if ($l -match '^>>>\s+Section start (\S+ \S+)') {
    $d = [datetime]::ParseExact($matches[1],'yyyy/MM/dd HH:mm:ss',$null)
    if ($d -ge $From -and $d -le $To -and $blk -match 'oem|u0|amd|display|inf') { "$($d.ToString('MM-dd HH:mm')) $blk" }
  }
}
"=== MSI-установки в окне ==="
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='MsiInstaller'; Id=1033,1034,11707,11724; StartTime=$From; EndTime=$To} |
  sort TimeCreated | % { "$($_.TimeCreated.ToString('MM-dd HH:mm')) $($_.Message.Substring(0,[Math]::Min(100,$_.Message.Length)))" }
"=== Текущий драйвер GPU (inf/версия/дата) ==="
Get-CimInstance Win32_PnPSignedDriver | ? { $_.DeviceClass -eq 'DISPLAY' } | select DeviceName,DriverVersion,DriverDate,InfName | fl
"=== Radeon: версия пакета ==="
Get-ItemProperty 'HKLM:\SOFTWARE\AMD\CN' | select -ExpandProperty 'RadeonSoftwareVersion' -ErrorAction SilentlyContinue
"=== TDR-таймаут и прочие TdrXXX в реестре ==="
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' | select Tdr* | fl
