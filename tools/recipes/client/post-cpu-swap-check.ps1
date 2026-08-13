# Контрольный срез после замены процессора (грабля: СЗ 160587 — 276 корректируемых MCE
# на одном ядре, проц поменяли по гарантии; надо доказать, что WHEA ушли, и отделить
# старую историю от новой). Печатает: текущий CPU, дату BIOS, uptime, полную сводку
# WHEA по ApicId с датой первого/последнего события и последние Kernel-Power 41.

$ErrorActionPreference = 'SilentlyContinue'

$cpu = Get-CimInstance Win32_Processor
$os  = Get-CimInstance Win32_OperatingSystem
$bios = Get-CimInstance Win32_BIOS

"=== CPU ==="
"{0} | {1} | ProcessorId={2} | Rev={3} | Stepping={4}" -f $cpu.Name.Trim(), $cpu.SocketDesignation, $cpu.ProcessorId, $cpu.Revision, $cpu.Stepping
"Cores={0} Threads={1} MaxClock={2}MHz" -f $cpu.NumberOfCores, $cpu.NumberOfLogicalProcessors, $cpu.MaxClockSpeed

"`n=== BIOS ==="
"{0} {1} от {2}" -f $bios.Manufacturer, $bios.SMBIOSBIOSVersion, $bios.ReleaseDate

"`n=== Uptime ==="
"Boot: {0} | Uptime: {1}" -f $os.LastBootUpTime, ((Get-Date) - $os.LastBootUpTime)

"`n=== Память (частота) ==="
Get-CimInstance Win32_PhysicalMemory | ForEach-Object {
  "{0} {1} | Speed={2} Configured={3} | {4} ГБ" -f $_.Manufacturer, $_.PartNumber, $_.Speed, $_.ConfiguredClockSpeed, [math]::Round($_.Capacity/1GB)
}

"`n=== WHEA (весь журнал, группировка по APIC ID) ==="
$whea = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger'} -ErrorAction SilentlyContinue
if (-not $whea) {
  "WHEA-событий нет вообще."
} else {
  "Всего: {0} | первое: {1} | последнее: {2}" -f $whea.Count, ($whea | Sort-Object TimeCreated | Select-Object -First 1).TimeCreated, ($whea | Sort-Object TimeCreated | Select-Object -Last 1).TimeCreated
  $whea | ForEach-Object {
    $apic = if ($_.Message -match 'APIC ID:\s*(\d+)') { $matches[1] } else { 'n/a' }
    [pscustomobject]@{ Id = $_.Id; Apic = $apic; Time = $_.TimeCreated }
  } | Group-Object Id, Apic | ForEach-Object {
    $g = $_.Group
    "Id={0} APIC={1} : {2} шт, с {3} по {4}" -f $g[0].Id, $g[0].Apic, $g.Count, ($g | Sort-Object Time | Select-Object -First 1).Time, ($g | Sort-Object Time | Select-Object -Last 1).Time
  }
}

"`n=== Kernel-Power 41 (последние 10) ==="
Get-WinEvent -FilterHashtable @{LogName='System'; Id=41} -MaxEvents 10 -ErrorAction SilentlyContinue |
  ForEach-Object {
    $bc = if ($_.Message -match 'BugcheckCode\D+(\d+)') { $matches[1] } else { '?' }
    "{0} | BugcheckCode={1}" -f $_.TimeCreated, $bc
  }

"`n=== Минидампы ==="
Get-ChildItem C:\Windows\Minidump\*.dmp -ErrorAction SilentlyContinue |
  Sort-Object LastWriteTime -Descending | Select-Object -First 5 |
  ForEach-Object { "{0} | {1} | {2} КБ" -f $_.Name, $_.LastWriteTime, [math]::Round($_.Length/1KB) }
