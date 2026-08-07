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

        // Секция отвечает на вопрос «на каком физическом диске лежит pagefile и здоров ли он».
        // На 111111 умирающий SATA HDD с единственным pagefile давал BSOD 0x154/0x1A, но по
        // отчёту это не собиралось: буквы отдельно, физдиски отдельно, `ReadErrors: 393` без
        // различения corrected/uncorrected (а все 393 были неисправимы), и поверх всего
        // `HealthStatus: Healthy` (бэклог п.27).
        Probe("storage", "Диски (SMART / здоровье / разделы / pagefile)", """
            Get-PhysicalDisk -ErrorAction SilentlyContinue |
                Select-Object DeviceId, FriendlyName, MediaType, BusType,
                    @{n='GB';e={[math]::Round($_.Size/1GB)}}, HealthStatus, OperationalStatus |
                Format-Table -Auto | Out-String

            "=== SMART / reliability counters ==="
            # Empty output here reads as 'disks are healthy' while it means 'no data':
            # Get-StorageReliabilityCounter needs admin rights (same trap as zero sensors, p.38).
            $relCount = 0
            Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object {
                $c = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
                if ($c) {
                    $script:relCount++
                    # Own verdict instead of reprinting Windows: 'Healthy' on a disk with 393
                    # uncorrectable errors is actively misleading.
                    $verdict = 'OK'
                    if ($c.ReadErrorsUncorrected -gt 0 -or $c.WriteErrorsUncorrected -gt 0) { $verdict = 'SUSPECT (est neispravimye oshibki)' }
                    elseif ($c.ReallocatedSectorsCount -gt 0) { $verdict = 'WATCH (est pereraspredelennye sektora)' }
                    [PSCustomObject]@{
                        Disk                 = $_.FriendlyName
                        DeviceId             = $_.DeviceId
                        TempC                = $c.Temperature
                        WearPct              = $c.Wear
                        PowerOnHours         = $c.PowerOnHours
                        ReadErrorsTotal      = $c.ReadErrorsTotal
                        ReadErrorsUncorrect  = $c.ReadErrorsUncorrected
                        WriteErrorsTotal     = $c.WriteErrorsTotal
                        WriteErrorsUncorrect = $c.WriteErrorsUncorrected
                        ReallocatedSectors   = $c.ReallocatedSectorsCount
                        VERDICT              = $verdict
                    }
                }
            } | Format-List | Out-String
            if ($relCount -eq 0) {
                "SMART-schetchiki NEDOSTUPNY (Get-StorageReliabilityCounter nichego ne vernul - obychno net prav administratora)."
                "VAZHNO: eto NE 'diski zdorovy', a 'dannyh net'."
            }

            "=== Bukva -> fizicheskiy disk ==="
            # Without this table you have to guess which letter sits on which disk by size.
            $map = @{}
            foreach ($p in (Get-Partition -ErrorAction SilentlyContinue | Where-Object DriveLetter)) {
                $d = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object DeviceId -eq $p.DiskNumber
                $map["$($p.DriveLetter):"] = [PSCustomObject]@{
                    Letter = "$($p.DriveLetter):"
                    Disk   = $p.DiskNumber
                    Model  = $(if ($d) { $d.FriendlyName } else { '?' })
                    Bus    = $(if ($d) { $d.BusType } else { '?' })
                    Media  = $(if ($d) { $d.MediaType } else { '?' })
                }
            }
            $map.Values | Sort-Object Letter | Format-Table -Auto | Out-String

            "=== Pagefile ==="
            $pf = @(Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue)
            if ($pf.Count -eq 0) { "Pagefile ne nayden (ili upravlyaetsya sistemoy bez fayla)." }
            else {
                foreach ($f in $pf) {
                    $letter = ($f.Name -split ':')[0] + ':'
                    $info = $map[$letter]
                    if ($info) {
                        "{0} - {1} MB (peak {2} MB) -> disk {3}: {4} ({5}, {6})" -f `
                            $f.Name, $f.AllocatedBaseSize, $f.PeakUsage, $info.Disk, $info.Model, $info.Bus, $info.Media
                    } else { "{0} - {1} MB" -f $f.Name, $f.AllocatedBaseSize }
                }
                "Podskazka: pagefile na diske s neispravimymi oshibkami chteniya => 0x154 UNEXPECTED_STORE_EXCEPTION / 0x1A MEMORY_MANAGEMENT. Lechitsya perenosom pagefile + zamenoy diska."
            }

            "=== Diskovye sobytiya (disk/Ntfs/volmgr) ==="
            # These never reach the events section (Critical/Error filter there), while
            # disk Id 7 'bad block on device' is direct proof of a dying disk.
            $diskEvents = @()
            foreach ($prov in @('disk','Ntfs','volmgr','storahci','stornvme')) {
                # A provider may not exist on this machine (no NVMe - no stornvme), and an
                # unknown ProviderName breaks Get-WinEvent even with SilentlyContinue.
                try { $diskEvents += @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName=$prov } -ErrorAction Stop) }
                catch { }
            }
            if ($diskEvents.Count -gt 0) {
                "TOTAL: {0}" -f $diskEvents.Count
                $diskEvents | Group-Object ProviderName, Id | Sort-Object Count -Descending | Select-Object -First 15 |
                    ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
                $diskEvents | Sort-Object TimeCreated -Descending | Select-Object -First 10 |
                    Select-Object TimeCreated, ProviderName, Id, @{n='Msg';e={($_.Message -split "`r?`n")[0]}} |
                    Format-Table -Auto | Out-String
            } else { "none" }

            "=== Toma ==="
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

        // Никогда не мешаем шумные и редкие события в одной выборке с общим MaxEvents: на
        // 160636 фильтр по Id 1001,41,6008,7,55,153,129 с -MaxEvents 40 вернул почти сплошной
        // Id=55 (Kernel-Processor-Power пишет по штуке на поток CPU), а Kernel-Power 41 не
        // попал вообще — и диагноз уехал на 180 градусов (бэклог п.31).
        Probe("events", "События: критические/ошибки + счётчики по Id", """
            $since = (Get-Date).AddDays(-7)
            "=== Schetchiki po Id (System, Critical/Error, 7 dney) ==="
            $sys = @(Get-WinEvent -FilterHashtable @{ LogName='System'; Level=1,2; StartTime=$since } -ErrorAction SilentlyContinue)
            if ($sys.Count -gt 0) {
                "TOTAL: {0}" -f $sys.Count
                $sys | Group-Object ProviderName, Id | Sort-Object Count -Descending | Select-Object -First 20 |
                    ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
            } else { "System: 0 sobytiy urovnya Critical/Error za 7 dney (yavnyy nol, a ne molchanie)" }

            "=== System (Critical/Error, poslednie 40) ==="
            $sys | Sort-Object TimeCreated -Descending | Select-Object -First 40 |
                Select-Object TimeCreated, Id, ProviderName, @{n='Message';e={($_.Message -split "`r?`n")[0]}} |
                Format-Table -Auto | Out-String
            if ($sys.Count -gt 40) { "... {0} earlier events not listed (schetchiki vyshe)" -f ($sys.Count - 40) }

            "=== Application (Critical/Error, 3 dnya) ==="
            $app = @(Get-WinEvent -FilterHashtable @{ LogName='Application'; Level=1,2; StartTime=(Get-Date).AddDays(-3) } -ErrorAction SilentlyContinue)
            if ($app.Count -gt 0) {
                "TOTAL: {0}" -f $app.Count
                $app | Sort-Object TimeCreated -Descending | Select-Object -First 25 |
                    Select-Object TimeCreated, Id, ProviderName, @{n='Message';e={($_.Message -split "`r?`n")[0]}} |
                    Format-Table -Auto | Out-String
            } else { "Application: 0 sobytiy urovnya Critical/Error za 3 dnya" }

            "=== Redkie kritichnye Id - BEZ limita, za vsyu istoriyu ==="
            # There are only a few of them, nothing to trim; absence must be an explicit zero.
            foreach ($q in @(
                @{ N='Kernel-Power 41 (avariynoe vyklyuchenie)'; P='Microsoft-Windows-Kernel-Power'; I=41 },
                @{ N='EventLog 6008 (gryaznoe zavershenie)';     P='EventLog';                       I=6008 },
                @{ N='BugCheck 1001 (BSOD)';                     P='Microsoft-Windows-WER-SystemErrorReporting'; I=1001 },
                @{ N='WHEA-Logger (apparatnye oshibki)';         P='Microsoft-Windows-WHEA-Logger';  I=$null }
            )) {
                $filter = @{ LogName='System'; ProviderName=$q.P }
                if ($q.I) { $filter['Id'] = $q.I }
                $found = @()
                try { $found = @(Get-WinEvent -FilterHashtable $filter -ErrorAction Stop) } catch { }
                if ($found.Count -gt 0) {
                    "{0}: {1} sht, pervoe {2:yyyy-MM-dd HH:mm}, poslednee {3:yyyy-MM-dd HH:mm}" -f `
                        $q.N, $found.Count, $found[-1].TimeCreated, $found[0].TimeCreated
                } else { "{0}: 0" -f $q.N }
            }
            """),

        // Kernel-Power 41 берём БЕЗ MaxEvents: лимит в 20 записей показывал только последние
        // дни и прятал главное — сколько всего вырубонов и когда был первый. На СЗ 160705
        // из-за этого дефект «приехал с завода» (первый hard-off через 7 минут после
        // установки ОС) читался как «сломалось в процессе эксплуатации». Событий этого типа
        // единицы-десятки, читать их все дёшево.
        Probe("reboots", "Перезагрузки: Kernel-Power 41 + dirty shutdown + BSOD-коды",
            BugcheckCodes.PowerShellPrologue() + HardwareWindow.PowerShellPrologue() + """
            "=== Okno etogo zheleza ==="
            Write-HwWindow

            "=== Kernel-Power 41 (unexpected reboot) - FULL HISTORY ==="
            $kpAll = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41 } -ErrorAction SilentlyContinue)
            # History from a portable service image belongs to OTHER machines: on 161432 it gave
            # 79 hard-offs and 12 MCE 'on one core' that had nothing to do with the request (p.92).
            $split = Split-ByHwWindow $kpAll
            $kp = @($split.Ours)
            if ($split.Foreign.Count -gt 0) {
                "VNIMANIE: {0} sobytiy 41 otbrosheno kak istoriya DRUGOGO zheleza (do {1:yyyy-MM-dd HH:mm})." -f $split.Foreign.Count, $SZ_HW_SINCE
                "  Ih daty: " + (($split.Foreign | Select-Object -First 5 | ForEach-Object { "{0:dd.MM.yyyy}" -f $_.TimeCreated }) -join ', ')
            }
            if ($kp.Count -gt 0) {
                $first = $kp[-1].TimeCreated; $last = $kp[0].TimeCreated
                "TOTAL: {0} events, first {1:yyyy-MM-dd HH:mm:ss}, last {2:yyyy-MM-dd HH:mm:ss}" -f $kp.Count, $first, $last
                $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
                if ($os -and $os.InstallDate) {
                    "OS installed: {0:yyyy-MM-dd HH:mm:ss}" -f $os.InstallDate
                }
                "--- per-day histogram ---"
                $kp | Group-Object { $_.TimeCreated.ToString('yyyy-MM-dd') } | Sort-Object Name |
                    ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }

                # PowerButtonTimestamp != 0 means the machine was switched off by the power
                # button - a normal shutdown, not a defect. On 161312 two of five 'unexpected
                # shutdowns' were exactly that, and the verdict changed with them (backlog p.93).
                $parsed = foreach ($e in $kp) {
                    $x = [xml]$e.ToXml(); $d = @{}; foreach ($p in $x.Event.EventData.Data) { $d[$p.Name] = $p.'#text' }
                    $pbt = 0
                    [void][UInt64]::TryParse("$($d['PowerButtonTimestamp'])", [ref]$pbt)
                    $bug = 0
                    [void][int]::TryParse("$($d['BugcheckCode'])", [ref]$bug)
                    [PSCustomObject]@{
                        Time  = $e.TimeCreated
                        Bug   = (Fmt-Bug $d['BugcheckCode'])
                        BugN  = $bug
                        Pbt   = $pbt
                        Sleep = $d['SleepInProgress']
                        Kind  = $(if ($bug -ne 0) { 'BSOD' } elseif ($pbt -ne 0) { 'knopka pitaniya' } else { 'hard-off' })
                    }
                }
                $parsed = @($parsed)
                $hard = @($parsed | Where-Object Kind -eq 'hard-off')
                $btn  = @($parsed | Where-Object Kind -eq 'knopka pitaniya')
                $bsod = @($parsed | Where-Object Kind -eq 'BSOD')
                "--- klassifikaciya (glavnoe) ---"
                "hard-off (nastoyashchiy obryv pitaniya): {0}" -f $hard.Count
                "knopka pitaniya (PowerButtonTimestamp != 0, NE defekt): {0}" -f $btn.Count
                "BSOD (BugcheckCode != 0): {0}" -f $bsod.Count
                if ($hard.Count -gt 0) {
                    "hard-off kogda: " + (($hard | Select-Object -First 10 | ForEach-Object { "{0:dd.MM HH:mm}" -f $_.Time }) -join ', ')
                    # 'Defect came with the machine' is counted by REAL hard-offs only: a power
                    # button press right after OS install proves nothing (p.93).
                    if ($os -and $os.InstallDate) {
                        $firstHard = ($hard | Sort-Object Time | Select-Object -First 1).Time
                        $age = $firstHard - $os.InstallDate
                        "First hard-off: {0:N1} h after OS install" -f $age.TotalHours
                        if ($age.TotalHours -lt 24) {
                            "!!! First hard-off within 24h of OS install => defect came with the machine, not caused by usage/software."
                        }
                    }
                }
                if ($btn.Count -gt 0 -and $hard.Count -eq 0) {
                    "VAZHNO: vse sobytiya 41 - vyklyucheniya knopkoy. Schitat ih vyrubonami NELZYA."
                }

                "--- last 20 events (details) ---"
                $parsed | Select-Object -First 20 | ForEach-Object {
                    "[{0}] {1} Bugcheck={2} PowerButtonTs={3} SleepInProgress={4}" -f `
                        $_.Time, $_.Kind, $_.Bug, $_.Pbt, $_.Sleep
                }
                if ($kp.Count -gt 20) { "... {0} earlier events not listed (see totals and histogram above)" -f ($kp.Count - 20) }
                "Podskazka: BugcheckCode=0, PowerButtonTs=0 i net BSOD/WHEA => zhestkiy obryv (pitanie/peregrev), a ne soft."
            } else { "Kernel-Power 41: 0 events na etom zheleze (net avariynyh vyrubonov v zhurnale)" }
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

        // WHEA читаем ЦЕЛИКОМ и в первую очередь агрегируем. На 160587 было 276 событий, и
        // весь диагноз лежал в сводке: все до единого — Cache Hierarchy Error строго на APIC
        // 4/5, то есть на ОДНОМ физическом ядре из шести. Прежняя секция печатала 40 строк
        // без APIC ID и без общего числа — по такому отчёту это невидимо (бэклог п.44).
        // Поля MCA (банк, MciStat, тип ошибки) раньше приходилось доставать отдельным exec
        // из EventData XML — теперь они в отчёте (п.18).
        Probe("whea", "Аппаратные ошибки железа (WHEA-Logger, все уровни)",
            CperDecoder.PowerShellPrologue() + HardwareWindow.PowerShellPrologue() + """
            "=== Okno etogo zheleza ==="
            Write-HwWindow
            $whea = @()
            try { $whea = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger' } -ErrorAction Stop) } catch { }
            # 12 'Cache Hierarchy Error' on APIC 6/7 from a portable image are a signature of
            # SOMEONE ELSE's CPU - they must not reach the summary (backlog p.92).
            $wheaSplit = Split-ByHwWindow $whea
            $whea = @($wheaSplit.Ours)
            if ($wheaSplit.Foreign.Count -gt 0) {
                "VNIMANIE: {0} sobytiy WHEA otbrosheno kak istoriya DRUGOGO zheleza (do {1:yyyy-MM-dd HH:mm})." -f $wheaSplit.Foreign.Count, $SZ_HW_SINCE
            }
            if ($whea.Count -eq 0) {
                "none (apparatnyh oshibok ne logirovalos - vazhno: proverili VSE urovni, ne tolko Error)"
            } else {
                $ERRTYPE = @{'8'='TLB Error';'9'='Cache Hierarchy Error';'10'='Bus/Interconnect Error';'11'='Memory Error'}
                $rows = foreach ($e in $whea) {
                    $d = @{}
                    try { $x = [xml]$e.ToXml(); foreach ($p in $x.Event.EventData.Data) { $d[$p.Name] = $p.'#text' } } catch { }
                    # Id=1 (fatal) has NO named fields at all - everything sits in the binary
                    # data section, so aggregation used to come out empty exactly where it
                    # mattered (backlog p.68). Fall back to the CPER record.
                    $cper = $null
                    if (-not $d['ErrorType'] -and -not $d['ApicId']) {
                        try { $cper = Parse-Cper (Get-CperBytes $e) } catch { }
                    }
                    if ($cper) {
                        if ($null -ne $cper.ApicId -and -not $d['ApicId']) { $d['ApicId'] = "$($cper.ApicId)" }
                        $d['CperSeverity'] = $cper.Severity
                        $d['CperNotify']   = $cper.Notification
                        $d['CperSections'] = (($cper.Sections | ForEach-Object { "$($_.Type) [$($_.Severity)]" }) -join ', ')
                    }
                    [PSCustomObject]@{
                        Time     = $e.TimeCreated
                        Id       = $e.Id
                        Level    = $e.LevelDisplayName
                        ApicId   = $d['ApicId']
                        CperSev  = $d['CperSeverity']
                        Channel  = $d['CperNotify']
                        CperSect = $d['CperSections']
                        Bank     = $d['MciBank']
                        ErrType  = $d['ErrorType']
                        MciStat  = $d['MciStat']
                        MciAddr  = $d['MciAddr']
                        MciMisc  = $d['MciMisc']
                        Segment  = $d['Segment']
                        Bus      = $d['PrimaryDeviceBusNumber']
                        Device   = $d['PrimaryDeviceNumber']
                        Function = $d['PrimaryDeviceFunctionNumber']
                    }
                }
                "=== SUMMARY (vsya istoriya) ==="
                "TOTAL: {0} events, first {1:yyyy-MM-dd HH:mm:ss}, last {2:yyyy-MM-dd HH:mm:ss}" -f `
                    $rows.Count, $rows[-1].Time, $rows[0].Time
                "--- by Id ---"
                $rows | Group-Object Id | Sort-Object Count -Descending | ForEach-Object { "Id {0}: {1}" -f $_.Name, $_.Count }
                "--- by Error Type ---"
                $typed = @($rows | Where-Object { $_.ErrType })
                if ($typed.Count -gt 0) {
                    $typed | Group-Object ErrType | Sort-Object Count -Descending |
                        ForEach-Object { "{0} ({1}): {2}" -f $_.Name, $(if ($ERRTYPE[$_.Name]) { $ERRTYPE[$_.Name] } else { 'unknown' }), $_.Count }
                } else {
                    "Imenovannyh poley ErrorType net (tipichno dlya Id=1) - smotri razbor binarnoy zapisi nizhe."
                }

                "--- by CPER (binarnaya zapis: kanal / sekcii / severity) ---"
                $cpered = @($rows | Where-Object { $_.Channel })
                if ($cpered.Count -gt 0) {
                    $cpered | Group-Object Channel | Sort-Object Count -Descending |
                        ForEach-Object { "kanal {0}: {1}" -f $_.Name, $_.Count }
                    $cpered | Group-Object CperSect | Sort-Object Count -Descending |
                        ForEach-Object { "sekcii {0}: {1}" -f $_.Name, $_.Count }
                    $cpered | Group-Object CperSev | Sort-Object Count -Descending |
                        ForEach-Object { "severity {0}: {1}" -f $_.Name, $_.Count }
                } else {
                    "Binarnuyu zapis razobrat ne udalos (net baytov CPER v svoystvah sobytiya)."
                }
                "--- by MCA bank ---"
                $rows | Where-Object { $_.Bank } | Group-Object Bank | Sort-Object Count -Descending |
                    ForEach-Object { "Bank {0}: {1}" -f $_.Name, $_.Count }
                "--- by APIC ID (Zen: APIC = 2*core + smt, t.e. APIC 4/5 => core 2) ---"
                $apic = $rows | Where-Object { $_.ApicId } | Group-Object ApicId | Sort-Object Count -Descending
                foreach ($g in $apic) {
                    $core = [math]::Floor([int]$g.Name / 2)
                    "APIC {0} (core {1}): {2}" -f $g.Name, $core, $g.Count
                }
                if ($apic.Count -gt 0) {
                    $cores = @($rows | Where-Object { $_.ApicId } | ForEach-Object { [math]::Floor([int]$_.ApicId / 2) } | Sort-Object -Unique)
                    if ($cores.Count -eq 1) {
                        "!!! VSE oshibki na odnom fizicheskom yadre (core {0}) - eto lokalizaciya defekta, a ne sluchaynost." -f $cores[0]
                    }
                }
                "--- by PCIe device (Segment/Bus/Device/Function) ---"
                $pcie = $rows | Where-Object { $_.Bus } | Group-Object { "{0}/{1}/{2}/{3}" -f $_.Segment, $_.Bus, $_.Device, $_.Function }
                if ($pcie) { $pcie | Sort-Object Count -Descending | ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count } } else { "none" }
                "--- by date ---"
                $rows | Group-Object { $_.Time.ToString('yyyy-MM-dd') } | Sort-Object Name |
                    ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
                ""
                "=== LAST 20 EVENTS (details) ==="
                $rows | Select-Object -First 20 | Format-Table Time, Id, ApicId, Bank, ErrType, MciStat, Channel, CperSev -Auto | Out-String
                if ($rows.Count -gt 20) { "... {0} earlier events not listed (see summary above)" -f ($rows.Count - 20) }
                "=== MciStat bit flags (last 5) ==="
                foreach ($r in ($rows | Select-Object -First 5 | Where-Object { $_.MciStat })) {
                    $v = 0
                    if ([UInt64]::TryParse(($r.MciStat -replace '^0x',''), [System.Globalization.NumberStyles]::HexNumber, $null, [ref]$v)) {
                        $flags = @()
                        if ($v -band ([UInt64]1 -shl 63)) { $flags += 'VAL' }
                        if ($v -band ([UInt64]1 -shl 62)) { $flags += 'OVER(oshibok bylo bolshe, chem bank uspel zapisat)' }
                        if ($v -band ([UInt64]1 -shl 61)) { $flags += 'UC(neispravimaya)' }
                        if ($v -band ([UInt64]1 -shl 60)) { $flags += 'EN' }
                        if ($v -band ([UInt64]1 -shl 59)) { $flags += 'MISCV' }
                        if ($v -band ([UInt64]1 -shl 58)) { $flags += 'ADDRV' }
                        if ($v -band ([UInt64]1 -shl 57)) { $flags += 'PCC(kontekst yadra isporchen)' }
                        "[{0}] {1} -> {2}; MCA error code 0x{3:X4}" -f $r.Time, $r.MciStat, ($flags -join ' '), ($v -band 0xFFFF)
                    }
                }
                "=== FULL TEXT of last 2 (component: CPU/PCIe/memory) ==="
                $whea | Select-Object -First 2 | ForEach-Object { ("[{0}] Id={1}" -f $_.TimeCreated, $_.Id); $_.Message }
            }
            """),

        // TDR и прочие живые дампы ядра BSOD не вызывают — машина продолжает работать, и в
        // Minidump ничего не ложится. На 160521 из-за этого отчёт по заявке «вылетает игра»
        // показал «всё чисто», хотя рядом лежали 14 WATCHDOG-дампов и LiveKernelEvent 0x141
        // в ту же секунду, что и краш игры (бэклог п.37/п.21).
        Probe("livekernel", "Живые дампы ядра (TDR / watchdog, без BSOD)",
            BugcheckCodes.PowerShellPrologue() + """
            # Windows writes LiveKernelEvent P1 in HEX without a prefix: '124' here means
            # 0x124 WHEA_UNCORRECTABLE_ERROR. The section used to print it raw, so 148 proofs
            # of a hardware error read as noise (backlog p.69).
            function Fmt-P1($p1) {
                if (-not $p1) { return '' }
                $h = ($p1 -replace '^0x','')
                $n = 0
                if (-not [int]::TryParse($h, [System.Globalization.NumberStyles]::HexNumber, $null, [ref]$n)) { return "$p1" }
                Fmt-Bug $n
            }
            "=== C:\Windows\LiveKernelReports ==="
            $lk = @(Get-ChildItem 'C:\Windows\LiveKernelReports' -Recurse -Filter *.dmp -ErrorAction SilentlyContinue)
            if ($lk.Count -gt 0) {
                "TOTAL: {0} files, first {1:yyyy-MM-dd}, last {2:yyyy-MM-dd}" -f `
                    $lk.Count, ($lk | Sort-Object LastWriteTime | Select-Object -First 1).LastWriteTime,
                    ($lk | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
                $lk | Sort-Object LastWriteTime -Descending | Select-Object -First 20 |
                    Select-Object LastWriteTime, Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}, DirectoryName |
                    Format-Table -Auto | Out-String
                "Podskazka: dampy >1 GB base64 ne tashit - zabirat cherez szcli pull s --max-mb."
            } else { "No live kernel dumps." }

            "=== LiveKernelEvent (WER, Application Id=1001) ==="
            $wer = @(Get-WinEvent -FilterHashtable @{ LogName='Application'; Id=1001 } -ErrorAction SilentlyContinue |
                Where-Object { $_.Message -match 'LiveKernelEvent' })
            if ($wer.Count -gt 0) {
                $evts = foreach ($e in $wer) {
                    $p1 = ''
                    if ($e.Message -match 'P1:\s*([0-9a-fA-Fx]+)') { $p1 = $matches[1] }
                    [PSCustomObject]@{ Time = $e.TimeCreated; P1 = $p1; Code = (Fmt-P1 $p1) }
                }
                $evts = @($evts | Sort-Object Time -Descending)
                "TOTAL: {0}, first {1:yyyy-MM-dd HH:mm:ss}, last {2:yyyy-MM-dd HH:mm:ss}" -f `
                    $evts.Count, $evts[-1].Time, $evts[0].Time

                "--- by code (hex + imya, ne syroy P1) ---"
                $evts | Group-Object Code | Sort-Object Count -Descending | ForEach-Object {
                    $g = $_.Group | Sort-Object Time
                    "{0} - {1} sobytiy ({2:dd.MM}-{3:dd.MM})" -f $_.Name, $_.Count, $g[0].Time, $g[-1].Time
                }

                # Timeline, not just counters: on 161312 '298 events, every day' turned out to be
                # bursts of 8 within ONE second - DWM takes diagnostic snapshots when a 3D window
                # closes, i.e. traces of a stress test stopping, not a defect (backlog p.94).
                # Discriminator: a dump file written around the same time.
                "--- gruppy po vremeni (pachka = >=3 sobytiy v odnu sekundu) ---"
                $groups = $evts | Group-Object { $_.Time.ToString('yyyy-MM-dd HH:mm:ss') } | Sort-Object Name -Descending
                $withDump = 0; $artifacts = 0
                $shown = 0
                foreach ($g in $groups) {
                    $t = [datetime]::ParseExact($g.Name, 'yyyy-MM-dd HH:mm:ss', $null)
                    # A dump written within +-2 minutes marks a REAL event.
                    $near = @($lk | Where-Object { [math]::Abs(($_.LastWriteTime - $t).TotalSeconds) -le 120 })
                    $real = $near.Count -gt 0
                    if ($real) { $withDump += $g.Count } else { $artifacts += $g.Count }
                    if ($shown -lt 25) {
                        $mark = if ($real) { "NASTOYASHEE (ryadom damp: {0})" -f $near[0].Name }
                                elseif ($g.Count -ge 3) { 'pachka bez dampa - veroyatno artefakt zakrytiya 3D-prilozheniya (stress-test)' }
                                else { 'bez dampa' }
                        "{0} x{1} [{2}] {3}" -f $g.Name, $g.Count, (($g.Group | Select-Object -First 1).Code), $mark
                        $shown++
                    }
                }
                if ($groups.Count -gt 25) { "... esche {0} grupp ne pokazano" -f ($groups.Count - 25) }
                "ITOGO: sobytiy s dampom {0}, veroyatnyh artefaktov {1} (iz {2})" -f $withDump, $artifacts, $evts.Count
                if ($withDump -eq 0 -and $evts.Count -gt 0) {
                    "VNIMANIE: ni odno sobytie ne podtverzhdeno dampom - schitat 'videopodsistema sypetsya' po etim cifram NELZYA (p.94)."
                }
            } else { "none" }

            "=== Display / GPU driver events (vklyuchaya Warning - TDR imenno tam) ==="
            # An unregistered provider breaks Get-WinEvent with 'The parameter is incorrect'
            # EVEN with -ErrorAction SilentlyContinue: an NVIDIA box has no amdkmdag and vice
            # versa. Hence every query gets its own try/catch.
            $gpu = @()
            foreach ($p in @('Display','amdkmdag','nvlddmkm','igfxn','amdwddmg')) {
                try { $gpu += @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName=$p } -ErrorAction Stop) }
                catch { }
            }
            if ($gpu.Count -gt 0) {
                "TOTAL: {0}" -f $gpu.Count
                $gpu | Group-Object Id | Sort-Object Count -Descending |
                    ForEach-Object { "Id {0}: {1}" -f $_.Name, $_.Count }
                $gpu | Sort-Object TimeCreated -Descending | Select-Object -First 10 |
                    Select-Object TimeCreated, Id, ProviderName, @{n='Msg';e={($_.Message -split "`r?`n")[0]}} |
                    Format-Table -Auto | Out-String
            } else { "none" }

            "=== Application crashes (Id=1000) ryadom po vremeni ==="
            $crash = @(Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='Application Error' } -ErrorAction SilentlyContinue |
                Select-Object -First 10)
            if ($crash.Count -gt 0) {
                foreach ($c in $crash) {
                    $near = $wer | Where-Object { [math]::Abs(($_.TimeCreated - $c.TimeCreated).TotalSeconds) -le 60 }
                    $mark = if ($near) { ' <== SOVPADAET s LiveKernelEvent (krash prilozheniya - SLEDSTVIE)' } else { '' }
                    "[{0}] {1}{2}" -f $c.TimeCreated, (($c.Message -split "`r?`n")[0]), $mark
                }
            } else { "none" }
            """),

        // Секция падала целиком с `ошибка: код 1:` и без единой подробности, а именно она
        // отделяет «хард-офф по питанию» от «краха драйвера» (бэклог п.74). Причины две:
        // Win32_ReliabilityRecords есть не на всякой машине (нужен работающий RACAgent), и
        // отсутствие C:\Windows\Minidump уводило шаг в ненулевой код — хотя «дампов нет» это
        // валидный ответ. Плюс без CrashControl ответ неинтерпретируем: «дампов нет» может
        // означать и «BSOD не было», и «дампы не пишутся вовсе».
        Probe("reliability", "История сбоев / BSOD / minidump'ы", """
            $ErrorActionPreference = 'Continue'
            "=== CrashControl (pishutsya li dampy voobshe) ==="
            try {
                $cc = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -ErrorAction Stop
                $kind = @{0='net dampov';1='complete';2='kernel';3='small (minidump)';7='automatic'}
                $k = $kind["$($cc.CrashDumpEnabled)"]
                "CrashDumpEnabled={0} ({1}), AutoReboot={2}, DumpFile={3}, MinidumpDir={4}" -f `
                    $cc.CrashDumpEnabled, $(if ($k) { $k } else { 'unknown' }), $cc.AutoReboot, $cc.DumpFile, $cc.MinidumpDir
                if ($cc.CrashDumpEnabled -eq 0) { "!!! Dampy otklyucheny: 'net dampov' zdes NICHEGO ne dokazyvaet." }
            } catch { "CrashControl nedostupen: $($_.Exception.Message)" }

            "=== Win32_ReliabilityRecords (poslednie 20) ==="
            try {
                $rr = @(Get-CimInstance Win32_ReliabilityRecords -ErrorAction Stop)
                if ($rr.Count -gt 0) {
                    $rr | Sort-Object TimeGenerated -Descending | Select-Object -First 20 |
                        Select-Object TimeGenerated, SourceName, @{n='Message';e={($_.Message -split "`r?`n")[0]}} |
                        Format-Table -Auto | Out-String
                } else { "Zapisey net (RACAgent mog byt otklyuchen - eto ne 'sboev ne bylo')." }
            } catch { "Win32_ReliabilityRecords nedostupen: $($_.Exception.Message)" }

            "=== Minidumps (C:\Windows\Minidump) ==="
            $md = @(Get-ChildItem 'C:\Windows\Minidump\*.dmp' -ErrorAction SilentlyContinue)
            if (-not (Test-Path 'C:\Windows\Minidump')) { "Papki C:\Windows\Minidump net - eto normalno, esli BSOD ne bylo." }
            if ($md.Count -gt 0) {
                $md | Sort-Object LastWriteTime -Descending |
                    Select-Object LastWriteTime, Name, @{n='KB';e={[math]::Round($_.Length/1KB)}} |
                    Format-Table -Auto | Out-String
            } else {
                # 'No minidumps' kept being read as 'no kernel crashes', while live TDR dumps
                # land elsewhere entirely and cause no BSOD (backlog p.37).
                "No minidumps. VAZHNO: 'net minidumpov' != 'net sboev yadra' - smotri sekciyu livekernel."
                $lkCount = @(Get-ChildItem 'C:\Windows\LiveKernelReports' -Recurse -Filter *.dmp -ErrorAction SilentlyContinue).Count
                if ($lkCount -gt 0) { "  -> v LiveKernelReports lezhit {0} zhivyh dampov yadra!" -f $lkCount }
                # Dumps may not be written at all - then 'no dumps' means nothing.
                $volmgr = @()
                try { $volmgr = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='volmgr'; Id=46 } -MaxEvents 3 -ErrorAction Stop) } catch { }
                if ($volmgr.Count -gt 0) { "  -> volmgr 46 'Crash dump initialization failed': dampy ne pishutsya v principe (p.14)" }
            }
            "=== MEMORY.DMP ==="
            $mem = Get-Item 'C:\Windows\MEMORY.DMP' -ErrorAction SilentlyContinue
            if ($mem) { "MEMORY.DMP: {0:yyyy-MM-dd HH:mm}, {1:N1} MB" -f $mem.LastWriteTime, ($mem.Length/1MB) }
            else { "MEMORY.DMP: net" }
            # Sekciya obyazana zavershatsya uspehom: 'dampov net' - eto otvet, a ne oshibka.
            exit 0
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
