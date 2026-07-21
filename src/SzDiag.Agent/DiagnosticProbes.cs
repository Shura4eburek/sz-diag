namespace SzDiag.Agent;

/// <summary>
/// Встроенный каталог read-only диагностических проб (секций). Каждая секция — обычный
/// command-шаг с Id = имя секции (для фильтра) и одной PowerShell-пробой. Не требует файла
/// на диске — работает всегда. Гоняется тем же TestRunner, отчёт строит DiagReportBuilder.
/// Claude запускает нужные секции точечно (`szcli diag run &lt;СЗ&gt; storage,events`), а не всё
/// пачкой. Все пробы обёрнуты в -ErrorAction SilentlyContinue / try-catch — падение одной
/// секции не срывает остальные и не даёт ненулевой код.
///
/// ВАЖНО: тело Run — строго ASCII. Скрипт уходит агенту в powershell.exe через stdin, а
/// PowerShell 5.1 на клиенте декодирует его в кодовой странице консоли (не UTF-8) — кириллица
/// в идентификаторах/литералах ломает парсер. Русские заголовки секций живут в Name (C#) и
/// попадают в diag.md (пишется UTF-8), а не в PowerShell.
/// </summary>
public static class DiagnosticProbes
{
    public static TestSuite Suite { get; } = new() { Steps = BuildSteps() };

    /// <summary>Имена всех секций (для подсказки/CLI-хелпа).</summary>
    public static IReadOnlyList<string> Sections { get; } =
        Suite.Steps.Where(s => s.Id is not null).Select(s => s.Id!).ToList();

    private static TestStep Probe(string id, string name, string run) =>
        new("command", name, Run: run, Id: id);

    private static IReadOnlyList<TestStep> BuildSteps() => new[]
    {
        Probe("system", "Система (ОС / модель / BIOS / uptime)", """
            $ci = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
            $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
            $bios = Get-CimInstance Win32_BIOS -ErrorAction SilentlyContinue
            [PSCustomObject]@{
                Manufacturer = $ci.Manufacturer
                Model        = $ci.Model
                OS           = $os.Caption
                Version      = "$($os.Version) (build $($os.BuildNumber))"
                Installed    = $os.InstallDate
                LastBoot     = $os.LastBootUpTime
                Uptime       = $(if ($os.LastBootUpTime) { (Get-Date) - $os.LastBootUpTime })
                BIOS         = "$($bios.SMBIOSBIOSVersion) $($bios.ReleaseDate)"
                Serial       = $bios.SerialNumber
            } | Format-List | Out-String
            try { "SecureBoot: " + (Confirm-SecureBootUEFI) } catch { "SecureBoot: n/a (non-UEFI or no rights)" }
            $tpm = Get-CimInstance -Namespace root/cimv2/security/microsofttpm -ClassName Win32_Tpm -ErrorAction SilentlyContinue
            if ($tpm) { "TPM: enabled=$($tpm.IsEnabled_InitialValue) spec=$($tpm.SpecVersion)" } else { "TPM: not found" }
            """),

        Probe("cpu", "Процессор", """
            Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue |
                Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed,
                    LoadPercentage, SocketDesignation |
                Format-List | Out-String
            """),

        Probe("memory", "Память (ОЗУ и модули)", """
            $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
            "Total: {0:N1} GB, Free: {1:N1} GB" -f ($os.TotalVisibleMemorySize/1MB), ($os.FreePhysicalMemory/1MB)
            Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue |
                Select-Object DeviceLocator, @{n='GB';e={[math]::Round($_.Capacity/1GB,1)}},
                    Speed, ConfiguredClockSpeed, Manufacturer, PartNumber |
                Format-Table -Auto | Out-String
            "Speed = pasportnaya (JEDEC), ConfiguredClockSpeed = fakticheskaya; ConfiguredClockSpeed > Speed => vklyuchen XMP/EXPO (razgon pamyati)."
            """),

        Probe("gpu", "Видеокарта (PCI ID для резолвера + драйвер)", """
            Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
                Select-Object Name, PNPDeviceID, DriverVersion, DriverDate,
                    @{n='VRAM_MB';e={[math]::Round($_.AdapterRAM/1MB)}},
                    @{n='Resolution';e={"$($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)"}} |
                Format-List | Out-String
            """),

        Probe("storage", "Диски (SMART / здоровье / разделы)", """
            Get-PhysicalDisk -ErrorAction SilentlyContinue |
                Select-Object DeviceId, FriendlyName, MediaType, BusType,
                    @{n='GB';e={[math]::Round($_.Size/1GB)}}, HealthStatus, OperationalStatus |
                Format-Table -Auto | Out-String
            Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object {
                $c = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
                if ($c) {
                    [PSCustomObject]@{
                        Disk               = $_.FriendlyName
                        TempC              = $c.Temperature
                        WearPct            = $c.Wear
                        PowerOnHours       = $c.PowerOnHours
                        ReadErrors         = $c.ReadErrorsTotal
                        WriteErrors        = $c.WriteErrorsTotal
                        ReallocatedSectors = $c.ReallocatedSectorsCount
                    }
                }
            } | Format-List | Out-String
            Get-Volume -ErrorAction SilentlyContinue | Where-Object DriveLetter |
                Select-Object DriveLetter, FileSystemLabel,
                    @{n='GB';e={[math]::Round($_.Size/1GB)}},
                    @{n='FreeGB';e={[math]::Round($_.SizeRemaining/1GB)}}, HealthStatus |
                Format-Table -Auto | Out-String
            """),

        Probe("temps", "Температуры (ACPI термозоны)", """
            try {
                Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction Stop |
                    ForEach-Object {
                        [PSCustomObject]@{ Zone = $_.InstanceName; TempC = [math]::Round(($_.CurrentTemperature/10)-273.15,1) }
                    } | Format-Table -Auto | Out-String
            } catch { "ACPI thermal zones unavailable (common on desktops): $($_.Exception.Message)" }
            """),

        Probe("drivers", "Проблемные устройства / драйверы", """
            $bad = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.Status -ne 'OK' }
            if ($bad) {
                $bad | Select-Object Status, Class, FriendlyName, InstanceId | Format-Table -Auto | Out-String
            } else { "No problem devices (all Status=OK)." }
            """),

        Probe("events", "События: критические/ошибки (System + Application)", """
            $since = (Get-Date).AddDays(-7)
            "=== System (Critical/Error, 7 days) ==="
            Get-WinEvent -FilterHashtable @{ LogName='System'; Level=1,2; StartTime=$since } -MaxEvents 40 -ErrorAction SilentlyContinue |
                Select-Object TimeCreated, Id, ProviderName, @{n='Message';e={($_.Message -split "`r?`n")[0]}} |
                Format-Table -Auto | Out-String
            "=== Application (Critical/Error, 3 days) ==="
            Get-WinEvent -FilterHashtable @{ LogName='Application'; Level=1,2; StartTime=(Get-Date).AddDays(-3) } -MaxEvents 25 -ErrorAction SilentlyContinue |
                Select-Object TimeCreated, Id, ProviderName, @{n='Message';e={($_.Message -split "`r?`n")[0]}} |
                Format-Table -Auto | Out-String
            """),

        Probe("reboots", "Перезагрузки: Kernel-Power 41 + dirty shutdown + BSOD-коды", """
            "=== Kernel-Power 41 (unexpected reboot, last 20) ==="
            $kp = Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41 } -MaxEvents 20 -ErrorAction SilentlyContinue
            if ($kp) {
                foreach ($e in $kp) {
                    $x = [xml]$e.ToXml(); $d = @{}; foreach ($p in $x.Event.EventData.Data) { $d[$p.Name] = $p.'#text' }
                    "[{0}] BugcheckCode={1} Param1={2} PowerButtonTs={3} SleepInProgress={4}" -f `
                        $e.TimeCreated, $d['BugcheckCode'], $d['BugcheckParameter1'], $d['PowerButtonTimestamp'], $d['SleepInProgress']
                }
                "Podskazka: BugcheckCode=0 i net BSOD/WHEA => zhestkiy obryv (pitanie/peregrev), a ne soft."
            } else { "none" }
            "=== Dirty shutdown / EventLog 6008/6005/6006 (last 20) ==="
            Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='EventLog'; Id=6008,6005,6006 } -MaxEvents 20 -ErrorAction SilentlyContinue |
                Select-Object TimeCreated, Id, @{n='Msg';e={($_.Message -split "`r?`n")[0]}} | Format-Table -Auto | Out-String
            "=== BugCheck 1001 (BSOD stop codes, last 10) ==="
            $bc = Get-WinEvent -FilterHashtable @{ LogName='System'; Id=1001; ProviderName='Microsoft-Windows-WER-SystemErrorReporting' } -MaxEvents 10 -ErrorAction SilentlyContinue
            if ($bc) { $bc | ForEach-Object { "[{0}] {1}" -f $_.TimeCreated, (($_.Message -split "`r?`n")[0]) } } else { "none (no BSOD)" }
            """),

        Probe("whea", "Аппаратные ошибки железа (WHEA-Logger, все уровни)", """
            $whea = Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger' } -MaxEvents 40 -ErrorAction SilentlyContinue
            if ($whea) {
                $whea | Select-Object TimeCreated, Id, LevelDisplayName, @{n='Msg';e={($_.Message -split "`r?`n")[0]}} |
                    Format-Table -Auto | Out-String
                "--- full text of last 3 WHEA (component: CPU/PCIe/memory) ---"
                $whea | Select-Object -First 3 | ForEach-Object { ("[{0}] Id={1}" -f $_.TimeCreated, $_.Id); $_.Message }
            } else { "none (apparatnyh oshibok ne logirovalos - vazhno: proverili VSE urovni, ne tolko Error)" }
            """),

        Probe("reliability", "История сбоев / BSOD / minidump'ы", """
            Get-CimInstance Win32_ReliabilityRecords -ErrorAction SilentlyContinue |
                Sort-Object TimeGenerated -Descending | Select-Object -First 20 |
                Select-Object TimeGenerated, SourceName, @{n='Message';e={($_.Message -split "`r?`n")[0]}} |
                Format-Table -Auto | Out-String
            "=== Minidumps (C:\Windows\Minidump) ==="
            $md = Get-ChildItem 'C:\Windows\Minidump\*.dmp' -ErrorAction SilentlyContinue
            if ($md) {
                $md | Sort-Object LastWriteTime -Descending |
                    Select-Object LastWriteTime, Name, @{n='KB';e={[math]::Round($_.Length/1KB)}} |
                    Format-Table -Auto | Out-String
            } else { "No minidumps." }
            """),

        Probe("battery", "Батарея (заряд и износ, ноутбуки)", """
            $b = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue
            if (-not $b) { "No battery (desktop)." }
            else {
                $b | Select-Object Name, EstimatedChargeRemaining, BatteryStatus | Format-List | Out-String
                try {
                    $sd = @(Get-CimInstance -Namespace root/wmi -ClassName BatteryStaticData -ErrorAction Stop)
                    $fc = @(Get-CimInstance -Namespace root/wmi -ClassName BatteryFullChargedCapacity -ErrorAction Stop)
                    0..($sd.Count - 1) | ForEach-Object {
                        $design = $sd[$_].DesignedCapacity; $full = $fc[$_].FullChargedCapacity
                        [PSCustomObject]@{
                            DesignCapacity = $design
                            FullCharge     = $full
                            WearPct        = $(if ($design) { [math]::Round(100 - ($full/$design*100), 1) })
                        }
                    } | Format-List | Out-String
                } catch { "Battery wear data unavailable: $($_.Exception.Message)" }
            }
            """),
    };
}
