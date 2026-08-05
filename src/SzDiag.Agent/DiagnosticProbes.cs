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

        // Kernel-Power 41 берём БЕЗ MaxEvents: лимит в 20 записей показывал только последние
        // дни и прятал главное — сколько всего вырубонов и когда был первый. На СЗ 160705
        // из-за этого дефект «приехал с завода» (первый hard-off через 7 минут после
        // установки ОС) читался как «сломалось в процессе эксплуатации». Событий этого типа
        // единицы-десятки, читать их все дёшево.
        Probe("reboots", "Перезагрузки: Kernel-Power 41 + dirty shutdown + BSOD-коды",
            BugcheckCodes.PowerShellPrologue() + """
            "=== Kernel-Power 41 (unexpected reboot) - FULL HISTORY ==="
            $kp = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41 } -ErrorAction SilentlyContinue)
            if ($kp.Count -gt 0) {
                $first = $kp[-1].TimeCreated; $last = $kp[0].TimeCreated
                "TOTAL: {0} events, first {1:yyyy-MM-dd HH:mm:ss}, last {2:yyyy-MM-dd HH:mm:ss}" -f $kp.Count, $first, $last
                $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
                if ($os -and $os.InstallDate) {
                    "OS installed: {0:yyyy-MM-dd HH:mm:ss}" -f $os.InstallDate
                    $age = $first - $os.InstallDate
                    "First unexpected shutdown: {0:N1} h after OS install" -f $age.TotalHours
                    if ($age.TotalHours -lt 24) {
                        "!!! First hard-off within 24h of OS install => defect came with the machine, not caused by usage/software."
                    }
                }
                "--- per-day histogram ---"
                $kp | Group-Object { $_.TimeCreated.ToString('yyyy-MM-dd') } | Sort-Object Name |
                    ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
                "--- last 20 events (details) ---"
                foreach ($e in ($kp | Select-Object -First 20)) {
                    $x = [xml]$e.ToXml(); $d = @{}; foreach ($p in $x.Event.EventData.Data) { $d[$p.Name] = $p.'#text' }
                    "[{0}] Bugcheck={1} Param1={2} PowerButtonTs={3} SleepInProgress={4}" -f `
                        $e.TimeCreated, (Fmt-Bug $d['BugcheckCode']), $d['BugcheckParameter1'], $d['PowerButtonTimestamp'], $d['SleepInProgress']
                }
                if ($kp.Count -gt 20) { "... {0} earlier events not listed (see totals and histogram above)" -f ($kp.Count - 20) }
                "Podskazka: BugcheckCode=0 i net BSOD/WHEA => zhestkiy obryv (pitanie/peregrev), a ne soft."
            } else { "Kernel-Power 41: 0 events (net avariynyh vyrubonov v zhurnale)" }
            "=== Dirty shutdown / EventLog 6008/6005/6006 (last 20) ==="
            $ds = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='EventLog'; Id=6008,6005,6006 } -ErrorAction SilentlyContinue)
            "TOTAL 6008 (dirty shutdown): {0}" -f @($ds | Where-Object { $_.Id -eq 6008 }).Count
            $ds | Select-Object -First 20 |
                Select-Object TimeCreated, Id, @{n='Msg';e={($_.Message -split "`r?`n")[0]}} | Format-Table -Auto | Out-String
            "=== BugCheck 1001 (BSOD stop codes) ==="
            $bc = @(Get-WinEvent -FilterHashtable @{ LogName='System'; Id=1001; ProviderName='Microsoft-Windows-WER-SystemErrorReporting' } -ErrorAction SilentlyContinue)
            if ($bc.Count -gt 0) {
                "TOTAL: {0} BSOD, first {1:yyyy-MM-dd HH:mm:ss}, last {2:yyyy-MM-dd HH:mm:ss}" -f $bc.Count, $bc[-1].TimeCreated, $bc[0].TimeCreated
                $bc | Select-Object -First 10 | ForEach-Object {
                    $code = ''
                    if ($_.Message -match '0x([0-9a-fA-F]{8})') { $code = Fmt-Bug ([Convert]::ToInt64($matches[1], 16)) }
                    "[{0}] {1}" -f $_.TimeCreated, $(if ($code) { $code } else { ($_.Message -split "`r?`n")[0] })
                }
            } else { "none (no BSOD)" }
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
