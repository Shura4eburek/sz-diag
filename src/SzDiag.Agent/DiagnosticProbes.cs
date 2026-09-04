using SzDiag.Contracts;

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

            "=== Uptime protiv realnoy raboty ==="
            # Uptime = now - LastBootUpTime i NE vychitaet son: na 161346 'Uptime 11 sutok'
            # uehal v pismo klientu kak '11 dib bez zboyiv', a mashina prospala v S3 pochti
            # vsyo eto vremya (p.132). Schitaem son summoy intervalov Kernel-Power 42 -> 107.
            if ($os.LastBootUpTime) {
                $sleepEv = @(Get-WinEvent -FilterHashtable @{ LogName='System';
                        ProviderName='Microsoft-Windows-Kernel-Power'; Id=42,107;
                        StartTime=$os.LastBootUpTime } -ErrorAction SilentlyContinue |
                    Sort-Object TimeCreated)
                $slept = [TimeSpan]::Zero
                $sleepStart = $null
                foreach ($e in $sleepEv) {
                    if ($e.Id -eq 42) { $sleepStart = $e.TimeCreated }
                    elseif ($e.Id -eq 107 -and $sleepStart) { $slept += ($e.TimeCreated - $sleepStart); $sleepStart = $null }
                }
                $up = (Get-Date) - $os.LastBootUpTime
                "Uptime {0:dd\.hh\:mm}, iz nih son {1:dd\.hh\:mm} => realnaya rabota {2:dd\.hh\:mm}" -f `
                    $up, $slept, ($up - $slept)
            }
            $hb = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
            $hf = Test-Path "$env:SystemDrive\hiberfil.sys"
            "Fast startup: HiberbootEnabled=$hb, hiberfil.sys=$hf" + $(if ("$hb" -eq '1' -and $hf) { " => uptime perezhivaet 'vyklyuchenie'!" } else { "" })
            "VAZHNO: uptime NE dokazyvaet rabotu. Narabotka = SMART PowerOnHours (sektsiya storage); chastotu otkazov schitat na chas narabotki, a ne na kalendarnyy den."

            "=== Poslednyaya zapis v zhurnale DO podklyucheniya ==="
            # Dyra v zhurnale srazu pokazyvaet, chto mashina stoyala (p.132): na zhivoy mashine
            # odin tolko Windows Update pishet desyatki strok za nedelyu, i pervaya zapis posle
            # dolgogo molchaniya - eto WU dogonyaet obnovleniya srazu posle podyoma.
            try {
                $lastSys = Get-WinEvent -LogName System -MaxEvents 1 -ErrorAction Stop
                $gap = (Get-Date) - $lastSys.TimeCreated
                "System log: poslednyaya zapis {0:yyyy-MM-dd HH:mm:ss} ({1}, Id={2}), razryv do seychas {3:N1} ch" -f `
                    $lastSys.TimeCreated, $lastSys.ProviderName, $lastSys.Id, $gap.TotalHours
            } catch { "System log: net dannyh - $($_.Exception.Message)" }
            try {
                $lastRel = Get-CimInstance Win32_ReliabilityRecords -ErrorAction Stop |
                    Sort-Object TimeGenerated -Descending | Select-Object -First 1
                if ($lastRel) {
                    "Reliability Records: poslednyaya zapis {0:yyyy-MM-dd HH:mm:ss}" -f $lastRel.TimeGenerated
                } else { "Reliability Records: pusto" }
            } catch { "Reliability Records: nedostupny - $($_.Exception.Message)" }
            """),

        // Один InstallDate ничего не доказывает: он переживает feature update и едет внутри
        // образа. На 161346 «ОС старше даты сборки, значит переносилась» на этом основании
        // ушло клиенту, а он потребовал письменное подтверждение — и его пришлось строить
        // ad-hoc рецептом os-provenance.ps1 (бэклог п.162). Прямые признаки: CloneTag
        // (метка снятия образа), GeneralizationState (sysprep обезличил систему), даты
        // setupapi.dev.log/профилей/тома (когда драйверы/OOBE прошли именно на ЭТОЙ сборке),
        // призраки чужого железа в Enum и статус активации.
        Probe("os", "Происхождение ОС (образ / sysprep / чистая установка)", """
            $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
            "=== Zayavlennaya versiya i InstallDate ==="
            if ($cv.InstallDate) {
                "InstallDate (registry): {0:yyyy-MM-dd HH:mm:ss} - PEREZHIVAET feature update i edet vnutri obraza, samo po sebe NICHEGO ne dokazyvaet." -f `
                    ([DateTimeOffset]::FromUnixTimeSeconds($cv.InstallDate).LocalDateTime)
            } else { "InstallDate: net dannyh" }
            "BuildLabEx obraza: {0}" -f $cv.BuildLabEx
            "InstallationType: {0}" -f $cv.InstallationType

            "=== Sledy klonirovaniya / sysprep ==="
            $ct = Get-ItemProperty 'HKLM:\SYSTEM\Setup' -Name CloneTag -ErrorAction SilentlyContinue
            if ($ct) { "CloneTag: {0} (PRYAMAYA metka snyatiya obraza)" -f ($ct.CloneTag -join ' | ') }
            else { "CloneTag: net" }
            $sp = Get-ItemProperty 'HKLM:\SYSTEM\Setup\Status\SysprepStatus' -ErrorAction SilentlyContinue
            if ($sp -and ($null -ne $sp.GeneralizationState)) {
                $gs = [int]$sp.GeneralizationState
                $meaning = @{7='obraz obezlichen (sysprep /generalize proshel)'; 4='ne obezlichen'}[$gs]
                "GeneralizationState: {0} ({1})" -f $gs, $(if ($meaning) { $meaning } else { 'unknown' })
            } else { "GeneralizationState: net dannyh (SysprepStatus ne nayden)" }
            "Windows.old: {0}" -f $(if (Test-Path 'C:\Windows.old') { 'EST (byla predydushaya ustanovka na etom diske)' } else { 'net' })

            "=== Data sozdaniya toma C: (edet vnutri obraza vmeste s faylami) ==="
            $vol = Get-Item -LiteralPath 'C:\System Volume Information' -Force -ErrorAction SilentlyContinue
            if ($vol) { "Tom C: sozdan {0:yyyy-MM-dd HH:mm:ss}" -f $vol.CreationTime } else { "net dostupa k System Volume Information" }

            "=== setupapi.dev.log - kogda na ETOY sisteme vpervye stavilis drayvery ==="
            $sa = 'C:\Windows\INF\setupapi.dev.log'
            if (Test-Path $sa) {
                $fi = Get-Item $sa -Force
                "sozdan {0:yyyy-MM-dd HH:mm:ss}" -f $fi.CreationTime
            } else { "fayla net" }

            "=== Profili polzovateley (data sozdaniya = pervyy vhod / OOBE na ETOY sisteme) ==="
            $profiles = @()
            Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $pp = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).ProfileImagePath
                    if ($pp -and $pp -notmatch 'systemprofile|LocalService|NetworkService') {
                        $d = Get-Item -LiteralPath $pp -Force -ErrorAction SilentlyContinue
                        if ($d) { $profiles += [PSCustomObject]@{ Profil = $pp; Sozdan = $d.CreationTime } }
                    }
                }
            if ($profiles.Count -gt 0) { $profiles | Sort-Object Sozdan | Format-Table -Auto | Out-String }
            else { "profiley polzovateley ne nayti" }

            "=== Prizraki chuzhogo zheleza (Status=Unknown, PCI/USB) ==="
            # Obraz s DRUGOY platformy ostavlyaet v Enum PCI-ustroystva, kotoryh v mashine net.
            $ghosts = @(Get-PnpDevice -ErrorAction SilentlyContinue |
                Where-Object { $_.Status -eq 'Unknown' -and $_.InstanceId -match '^(PCI|USB\\VID)' })
            "Prizrakov PCI/USB: {0}" -f $ghosts.Count
            if ($ghosts.Count -gt 0) {
                $ghosts | Select-Object -First 20 | ForEach-Object { "  {0} {1}" -f $_.Class, $_.FriendlyName }
                "VAZHNO: prizraki ne dokazyvayut chuzhoe zhelezo naprjamuyu - eto mogut byt i sobstvennye otklyuchennye ustroystva."
            } else { "0 - argument PROTIV versii pro chuzhie drayvery/zhelezo v obraze." }

            "=== Aktivaciya ==="
            try {
                $lic = @(Get-CimInstance SoftwareLicensingProduct -ErrorAction Stop |
                    Where-Object { $_.PartialProductKey })
                $names = @{0='Unlicensed';1='Licensed';2='OOBGrace';3='OOTGrace';4='NonGenuineGrace';5='NotificationMode';6='ExtendedGrace'}
                if ($lic.Count -gt 0) {
                    foreach ($l in $lic) {
                        $st = [int]$l.LicenseStatus
                        "Aktivaciya: status={0} ({1}), kanal={2}, opisanie={3}" -f `
                            $st, $(if ($names[$st]) { $names[$st] } else { 'unknown' }), $l.ProductKeyChannel, $l.Description
                    }
                } else { "Aktivaciya: produkt s klyuchom ne nayden" }
            } catch { "Aktivaciya: dannyh net ($($_.Exception.Message))" }

            "=== VYVOD ==="
            "CloneTag i/ili GeneralizationState=7 => sistema razvernuta iz obraza (obezlichena)."
            "setupapi.dev.log/profil polzovatelya POZZHE InstallDate => drayvery i OOBE proshli UZHE na etoy sborke."
            "Prizrakov 0 => argument PROTIV versii pro chuzhie drayvery/zhelezo v obraze."
            """),

        Probe("cpu", "Процессор", """
            Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue |
                Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed,
                    LoadPercentage, SocketDesignation |
                Format-List | Out-String
            """),

        // Chastoty odni ne otvechayut na vopros "vklyuchen li profil": na 160467
        // Speed=ConfiguredClockSpeed=4800, i tolko ConfiguredVoltage=1100 (JEDEC) skazal,
        // chto EXPO NE vklyuchen. VSOC (AM5, glavnyy ubiytsa IMC pri EXPO 6000) trebuet
        // lhmmon i otdelnogo zahoda (HVCI ego blokiruet - sm. tools/recipes) - vne scope
        // etoy proby (backlog p.8).
        Probe("memory", "Память (ОЗУ и модули)", """
            $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
            "Total: {0:N1} GB, Free: {1:N1} GB" -f ($os.TotalVisibleMemorySize/1MB), ($os.FreePhysicalMemory/1MB)

            # Klyuch po odnomu DeviceLocator skhlopyvaet raznye planki: na ASUS TUF B850-PLUS
            # WIFI obe planki reportyat DeviceLocator='DIMM 1', razlichayutsya tolko BankLabel.
            # Na 161211 eto stoilo poteryannoy planki v pasporte (2x32 -> 1x32, backlog p.200).
            # Kazhdyy fizicheskiy modul - svoya stroka, bez skhlopyvaniya po odnomu polyu.
            $mems = @(Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue |
                Sort-Object BankLabel, DeviceLocator, SerialNumber)
            $mems | Select-Object BankLabel, DeviceLocator, SerialNumber,
                    @{n='GB';e={[math]::Round($_.Capacity/1GB,1)}},
                    Speed, ConfiguredClockSpeed, ConfiguredVoltage, MinVoltage, MaxVoltage,
                    Manufacturer, PartNumber |
                Format-Table -Auto | Out-String

            $totalGb = [math]::Round(($mems | Measure-Object Capacity -Sum).Sum / 1GB)
            $winGb = [math]::Round(((Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).TotalPhysicalMemory / 1GB))
            "ITOGO: {0} planok, {1} GB summarno (Windows (TotalPhysicalMemory) vidit {2} GB)" -f $mems.Count, $totalGb, $winGb
            if ([math]::Abs($totalGb - $winGb) -gt 1) {
                "VNIMANIE: raskhozhdenie summy planok i togo, chto vidit Windows - proverit, ne skhlopnulis li planki po odinakovomu DeviceLocator (sm. BankLabel vyshe)."
            }

            "Speed = pasportnaya (JEDEC), ConfiguredClockSpeed = fakticheskaya; ConfiguredClockSpeed > Speed => vklyuchen XMP/EXPO (razgon pamyati)."
            "ConfiguredVoltage (mV): ~1100 = JEDEC (stok), ~1350-1400 = EXPO/XMP profil vklyuchen. VSOC (AM5) etoy probay ne snimaetsya - sm. lhmmon otdelnym zahodom DO stressa (HVCI ego blokiruet, backlog p.8)."
            """),

        // Pasport dlya zayavki v ASC: SUBSYS i part number vBIOS ne otdavala ni odna
        // sektsiya (backlog p.146, SZ 160705) - snimali otdelnym retseptom uzhe pod progonom.
        // HardwareInformation.* v reestre - REG_BINARY s ASCII vnutri: bez dekodirovaniya
        // poluchish prostynyu trehznachnyh chisel vmesto '115-D754BP0-101'.
        Probe("gpu", "Видеокарта (паспорт: SUBSYS/vBIOS/PCIe для заявки в АСЦ)", """
            Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
                Select-Object Name, PNPDeviceID, DriverVersion, DriverDate,
                    @{n='VRAM_MB';e={[math]::Round($_.AdapterRAM/1MB)}},
                    @{n='Resolution';e={"$($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)"}} |
                Format-List | Out-String

            "=== SUBSYS (dlya zayavki v ASC - otlichaet partnerskuyu platu ot referensa) ==="
            Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object {
                if ($_.PNPDeviceID -match 'SUBSYS_([0-9A-Fa-f]{8})') {
                    $s = $matches[1]
                    "SUBSYS_$s (subvendor=$($s.Substring(4,4)) subdevice=$($s.Substring(0,4)))"
                } else { "SUBSYS ne nayden v PNPDeviceID: $($_.PNPDeviceID)" }
            }

            "=== vBIOS / tochnaya plata (registr HardwareInformation.*) ==="
            function Convert-HwBytes($v) {
                if ($null -eq $v) { return $null }
                if ($v -is [string]) { return $v }
                ((($v | ForEach-Object { [char][int]$_ }) -join '') -replace "`0", '').Trim()
            }
            Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' -ErrorAction SilentlyContinue |
                Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
                    $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                    if ($p.DriverDesc) {
                        "DriverDesc: $($p.DriverDesc)"
                        foreach ($k in @('AdapterString','BiosString','ChipType','DacType','MemorySize')) {
                            $val = Convert-HwBytes $p."HardwareInformation.$k"
                            if ($val) { "  $k = $val" }
                        }
                        if ($p.MatchingDeviceId) { "  MatchingDeviceId = $($p.MatchingDeviceId)" }
                    }
                }

            "=== PCIe: shirina i skorost linii (tekushaya / maksimalnaya) ==="
            Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | ForEach-Object {
                $d = $_
                "Device: $($d.FriendlyName) [$($d.Status)]"
                foreach ($k in @('DEVPKEY_PciDevice_CurrentLinkSpeed','DEVPKEY_PciDevice_CurrentLinkWidth','DEVPKEY_PciDevice_MaxLinkSpeed','DEVPKEY_PciDevice_MaxLinkWidth')) {
                    $v = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName $k -ErrorAction SilentlyContinue).Data
                    if ($null -ne $v) { "  $($k -replace 'DEVPKEY_PciDevice_','') = $v" }
                }
            }

            "=== TDR / padeniya videodrayvera (sobytiya 4101, 4098, 14, 13) ==="
            $tdr = @(Get-WinEvent -FilterHashtable @{ LogName='System'; Id=4101,4098,14,13 } -MaxEvents 200 -ErrorAction SilentlyContinue |
                Where-Object { $_.ProviderName -match 'Display|amdkmdap|nvlddmkm|amdwddmg' })
            if ($tdr.Count -eq 0) { "TDR/padenij videodrayvera net" }
            else {
                "TOTAL TDR: $($tdr.Count)"
                $tdr | Select-Object -First 10 TimeCreated, Id, ProviderName | Format-Table -Auto | Out-String
            }
            """),

        // Секция отвечает на вопрос «на каком физическом диске лежит pagefile и здоров ли он».
        // На 111111 умирающий SATA HDD с единственным pagefile давал BSOD 0x154/0x1A, но по
        // отчёту это не собиралось: буквы отдельно, физдиски отдельно, `ReadErrors: 393` без
        // различения corrected/uncorrected (а все 393 были неисправимы), и поверх всего
        // `HealthStatus: Healthy` (бэклог п.27).
        Probe("storage", "Диски (SMART / здоровье / разделы / pagefile)",
            NvmeSmart.PowerShellPrologue() + DiskNumberHistory.PowerShellPrologue() + """
            Get-PhysicalDisk -ErrorAction SilentlyContinue |
                Select-Object DeviceId, FriendlyName, MediaType, BusType,
                    @{n='GB';e={[math]::Round($_.Size/1GB)}}, HealthStatus, OperationalStatus,
                    CanPool, CannotPoolReason, Usage |
                Format-Table -Auto | Out-String

            "=== Storage Spaces (disk mozhet byt fizicheski ispraven, no vypal iz obychnogo diskovogo steka) ==="
            # Get-Disk/diskpart/Win32_DiskDrive NE pokazyvayut disk, sostoyashiy v poole Storage
            # Spaces - on est v Get-PhysicalDisk (CanPool=False, CannotPoolReason='In a Pool'),
            # no vypadaet iz karty HarddiskN celikom. Na 111111 ispravnyy HDD 1TB v PUSTOM poole
            # (0 virtualnyh diskov) vyglyadel propavshim - razdel sozdat bylo nelzya (backlog p.239).
            $physAll = @(Get-PhysicalDisk -ErrorAction SilentlyContinue)
            $diskNums = @(Get-Disk -ErrorAction SilentlyContinue | ForEach-Object { $_.Number })
            $inPool = @($physAll | Where-Object { $diskNums -notcontains $_.DeviceId })
            if ($inPool.Count -gt 0) {
                foreach ($p in $inPool) {
                    "V POOLE Storage Spaces: {0} (SN {1}) - CanPool={2}, CannotPoolReason={3}, Usage={4}. Obychnomu diskovomu steku NE otdan (net v Get-Disk/diskpart/HarddiskN)." -f `
                        $p.FriendlyName, ("$($p.SerialNumber)".Trim()), $p.CanPool, $p.CannotPoolReason, $p.Usage
                }
            } else { "diskov, vypavshih iz Get-Disk v pool, ne naydeno" }

            $pools = @(Get-StoragePool -ErrorAction SilentlyContinue | Where-Object { -not $_.IsPrimordial })
            if ($pools.Count -eq 0) { "nepervichnyh poolov (sozdannyh polzovatelem) net" }
            else {
                foreach ($pool in $pools) {
                    $vds = @($pool | Get-VirtualDisk -ErrorAction SilentlyContinue)
                    $members = @($pool | Get-PhysicalDisk -ErrorAction SilentlyContinue)
                    $verdict = if ($vds.Count -eq 0) { ' - POOL PUSTOY, kandidat na Remove-StoragePool + vozvrat diska v obychnyy stek' } else { '' }
                    "pool '{0}': {1} fizicheskih diskov, {2} virtualnyh diskov{3}" -f $pool.FriendlyName, $members.Count, $vds.Count, $verdict
                    foreach ($m in $members) { "   disk: {0} (SN {1})" -f $m.FriendlyName, ("$($m.SerialNumber)".Trim()) }
                }
            }

            "=== Disks Offline/ReadOnly (fizicheski disk est, a razmetit nelzya) ==="
            $badState = @(Get-Disk -ErrorAction SilentlyContinue | Where-Object { $_.IsOffline -or $_.IsReadOnly })
            if ($badState.Count -gt 0) {
                $badState | Select-Object Number, FriendlyName, IsOffline, IsReadOnly, OperationalStatus |
                    Format-Table -Auto | Out-String
            } else { "diskov v Offline/ReadOnly net" }

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

            "=== NVMe SMART (Health Information Log 02h) ==="
            # Get-StorageReliabilityCounter na NVMe otdaet tolko TempC/Wear: PowerOnHours,
            # oshibki i Unsafe Shutdowns prihodyat PUSTYMI, a imenno Unsafe Shutdowns byl
            # glavnym dokazatelstvom v pretenzii na 161346 (p.120/142). Log 02h chitaetsya
            # naprjamuyu cherez IOCTL_STORAGE_QUERY_PROPERTY / StorageDeviceProtocolSpecificProperty
            # (Get-NvmeSmartRows - obshaya s sektsiey reboots, p.142).
            try {
                $nvmeRows = @(Get-NvmeSmartRows)
                if ($nvmeRows.Count -eq 0) { "NVMe diskov net." }
                foreach ($r in $nvmeRows) {
                    if ($r.ReadError) { $r.ReadError; continue }
                    $r | Format-List | Out-String
                }
            } catch { "NVMe SMART nedostupen: $($_.Exception.Message) - eto 'dannyh net', a ne 'disk zdorov'." }

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

            "=== Karta \Device\HarddiskN i RaidPortN -> fizicheskiy disk ==="
            # Sobytiya diska ssylayutsya na \Device\Harddisk1\DR1 / \Device\RaidPort2 - pri dvuh
            # NVMe odnogo vendora ponyat, KAKOY fizicheski disk sypletsya, bez etoy karty
            # nelzya (p.122; na 161346 klientskiy i zakazannyy putalis do zerkalnoy privyazki).
            # VAZHNO: karta - 'na seychas'; k sobytiyam do perestanovki diskov primenyat s
            # ogovorkoy (p.133).
            $dmap = @{}
            foreach ($dd in @(Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue)) {
                $dmap[[int]$dd.Index] = $dd
                "Harddisk{0} = {1} [SN {2}] {3}, {4} GB, SCSIPort{5} Target{6}" -f `
                    $dd.Index, $dd.Model, ("$($dd.SerialNumber)".Trim()), $dd.InterfaceType, `
                    [math]::Round($dd.Size/1GB), $dd.SCSIPort, $dd.SCSITargetId
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
                    Select-Object TimeCreated, ProviderName, Id, @{n='Msg';e={
                        $m = ($_.Message -split "`r?`n")[0]
                        # Model ryadom s \Device\HarddiskN - chtoby ne gadat po nomeru (p.122).
                        if ($m -match 'Harddisk(\d+)' -and $dmap[[int]$Matches[1]]) {
                            $m += ' [' + $dmap[[int]$Matches[1]].Model + ']'
                        }
                        $m
                    }} |
                    Format-Table -Auto | Out-String
            } else { "none" }

            "=== Svodka diskovyh sobytiy po Harddisk N (rezolv NA MOMENT SOBYTIYA) ==="
            # 396 sobytiy 'disk Id=51' na 160705 chut ne uehali v akt klientu kak 'oshibok
            # nakopitelya net' - ni odna sektsiya ih ne agregirovala i ne privyazyvala k
            # ustroystvu (backlog p.141). Nomer HarddiskN/RaidPortN plyvet ot zagruzki k
            # zagruzke (poryadok podklyucheniya) - karta 'na seychas' primenennaya k arhivnoy
            # oshibke mozhet dat ZERKALNUYU privyazku: na 161346 diski fizicheski pomenyalis
            # mestami mezhdu sobytiyami i sverkoy (backlog p.133). Rezolvim po istorii
            # Partition/Diagnostic 1006 na moment KAZHDOGO sobytiya, ne po tekushchey karte.
            if ($diskEvents.Count -gt 0) {
                $diskHistory = Get-DiskNumberHistory
                $kp41Times = @()
                try {
                    $kp41Times = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41 } -ErrorAction Stop |
                        Select-Object -ExpandProperty TimeCreated)
                } catch { }

                $rows = foreach ($e in $diskEvents) {
                    $diskNum = $null
                    if ($e.Message -match 'Harddisk(\d+)') { $diskNum = [int]$Matches[1] }
                    $resolved = $null
                    if ($null -ne $diskNum) { $resolved = Resolve-DiskAtTime $diskHistory $diskNum $e.TimeCreated }
                    # Gruppiruem NE po nomeru, a po (nomer + serial na tot moment) - esli
                    # slot pomenyal fizicheskiy disk vnutri okna, oni ne skhlopnutsya v odnu
                    # stroku s odnim 'model=' na oba.
                    $identity = if ($resolved) { "$diskNum|$($resolved.Serial)" } else { "$diskNum|?" }
                    $nearShutdown = $false
                    if ($kp41Times.Count -gt 0) {
                        $nearShutdown = [bool]@($kp41Times | Where-Object { [math]::Abs(($_ - $e.TimeCreated).TotalMinutes) -le 5 }).Count
                    }
                    [PSCustomObject]@{ Time = $e.TimeCreated; Disk = $diskNum; Identity = $identity; Resolved = $resolved; NearShutdown = $nearShutdown }
                }
                foreach ($g in ($rows | Group-Object Identity | Sort-Object Count -Descending)) {
                    $sample = $g.Group[0]
                    $label = if ($null -ne $sample.Disk) { "Harddisk$($sample.Disk)" } else { "(nomer diska ne opredelen iz Message)" }
                    $modelText = if ($sample.Resolved) { "{0} [SN {1}]" -f $sample.Resolved.Model, $sample.Resolved.Serial }
                                 else { "model NEIZVESTEN na tu datu (istorii Partition/Diagnostic 1006 net)" }
                    # Sovpadenie serial s TEKUSHCHIM USB-diskom - eto semnyy nositel, k
                    # defektu sistemnogo diska otnosheniya obychno ne imeet (backlog p.141).
                    $removableMark = ''
                    if ($sample.Resolved -and $sample.Resolved.Serial) {
                        $curMatch = @($dmap.Values | Where-Object { "$($_.SerialNumber)".Trim() -eq $sample.Resolved.Serial }) | Select-Object -First 1
                        if ($curMatch -and $curMatch.InterfaceType -eq 'USB') { $removableMark = ' [SEMNYY NOSITEL - USB]' }
                    }
                    $first = ($g.Group | Sort-Object Time | Select-Object -First 1).Time
                    $last = ($g.Group | Sort-Object Time -Descending | Select-Object -First 1).Time
                    $nearCount = @($g.Group | Where-Object NearShutdown).Count
                    "{0}: {1} sobytiy, {2:dd.MM.yyyy}-{3:dd.MM.yyyy}, {4}{5}, ryadom s Kernel-Power 41 (+-5 min): {6}" -f `
                        $label, $g.Count, $first, $last, $modelText, $removableMark, $nearCount
                }
                "Podskazka: sobytiya semnyh nositeley (USB-fleshki i pr.) k defektu sistemnogo diska"
                "otnosheniya NE imeyut - eto ne 'oshibok nakopitelya net', a 'oshibki na drugom ustroystve'."

                "--- Smena nomerov diskov za dostupnuyu istoriyu (Partition/Diagnostic 1006) ---"
                Write-DiskSlotSwaps $diskHistory
            }

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

        // Get-PnpDevice БЕЗ -PresentOnly возвращает ВСЮ историю устройств, когда-либо
        // подключавшихся под этой ОС — на 161190 (Ryzen 5 3600 + RTX 3050) секция напечатала
        // 300+ строк, среди них 9800X3D x16, 7800X3D x16, 7500F x12, ASUS AURA, Gigabyte
        // A620M: железо ДРУГИХ сборок, на которых гонялся тот же переносной образ сервиса
        // (тот же hostname DESKTOP-5GUF215 встречается на 160697 и 160587). Вывод читался
        // как "на машине куча проблемных устройств" (бэклог п.167).
        Probe("drivers", "Проблемные устройства / драйверы", """
            $all = @(Get-PnpDevice -ErrorAction SilentlyContinue)
            $present = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue)
            # Ustroystvo bez Status voobshe (ne 'OK', ne 'Error' - pusto) - eto ne 'Unknown'
            # problema, a otsutstvie dannyh; pechatat ego kak problemnoe nelzya.
            $bad = @($present | Where-Object { $_.Status -and $_.Status -ne 'OK' })
            if ($bad.Count -gt 0) {
                $bad | Select-Object Status, Class, FriendlyName, InstanceId | Format-Table -Auto | Out-String
            } else { "No problem devices among devices present now (all Status=OK)." }
            $ghosts = $all.Count - $present.Count
            if ($ghosts -gt 0) {
                "prizrakov proshlogo zheleza: {0} (ustroystva otsutstvuyut seychas - istoriya DRUGOY sborki, na kotoroy gonyalsya etot obraz)" -f $ghosts
            }
            """),

        // Никогда не мешаем шумные и редкие события в одной выборке с общим MaxEvents: на
        // 160636 фильтр по Id 1001,41,6008,7,55,153,129 с -MaxEvents 40 вернул почти сплошной
        // Id=55 (Kernel-Processor-Power пишет по штуке на поток CPU), а Kernel-Power 41 не
        // попал вообще — и диагноз уехал на 180 градусов (бэклог п.31).
        Probe("events", "События: критические/ошибки + счётчики по Id",
            EventWindow.PowerShellPrologue() + TimeZoneNote.PowerShellPrologue() + """
            Write-TzNote
            Write-EventWindowNote
            $since = (Get-Date).AddDays(-$SZ_EVENT_WINDOW_DAYS)
            "=== Schetchiki po Id (System, Critical/Error, $SZ_EVENT_WINDOW_DAYS dney) ==="
            $sys = @(Get-WinEvent -FilterHashtable @{ LogName='System'; Level=1,2; StartTime=$since } -ErrorAction SilentlyContinue)
            if ($sys.Count -gt 0) {
                "TOTAL: {0}" -f $sys.Count
                $sys | Group-Object ProviderName, Id | Sort-Object Count -Descending | Select-Object -First 20 |
                    ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
            } else { "System: 0 sobytiy urovnya Critical/Error za $SZ_EVENT_WINDOW_DAYS dney (yavnyy nol, a ne molchanie)" }

            "=== System (Critical/Error, poslednie 40) ==="
            $sys | Sort-Object TimeCreated -Descending | Select-Object -First 40 |
                Select-Object TimeCreated, Id, ProviderName, @{n='Message';e={($_.Message -split "`r?`n")[0]}} |
                Format-Table -Auto | Out-String
            if ($sys.Count -gt 40) { "... {0} earlier events not listed (schetchiki vyshe)" -f ($sys.Count - 40) }

            "=== Application (Critical/Error, $SZ_EVENT_WINDOW_DAYS dney) ==="
            $app = @(Get-WinEvent -FilterHashtable @{ LogName='Application'; Level=1,2; StartTime=$since } -ErrorAction SilentlyContinue)
            if ($app.Count -gt 0) {
                "TOTAL: {0}" -f $app.Count
                $app | Sort-Object TimeCreated -Descending | Select-Object -First 25 |
                    Select-Object TimeCreated, Id, ProviderName, @{n='Message';e={($_.Message -split "`r?`n")[0]}} |
                    Format-Table -Auto | Out-String
            } else { "Application: 0 sobytiy urovnya Critical/Error za $SZ_EVENT_WINDOW_DAYS dney" }

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
            EventWindow.PowerShellPrologue() + TimeZoneNote.PowerShellPrologue() + BugcheckCodes.PowerShellPrologue() +
            HardwareWindow.PowerShellPrologue() + NvmeSmart.PowerShellPrologue() + """
            Write-TzNote
            Write-JournalDepthNote
            "=== Okno etogo zheleza ==="
            Write-HwWindow

            "=== Kernel-Power 41 (unexpected reboot) - FULL HISTORY ==="
            $kpAll = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41 } -ErrorAction SilentlyContinue)
            # History from a portable service image belongs to OTHER machines: on 161432 it gave
            # 79 hard-offs and 12 MCE 'on one core' that had nothing to do with the request (p.92).
            $split = Split-ByHwWindow $kpAll
            $kp = @($split.Ours)
            # Dva otdelnyh bloka, a ne odna stroka s count (backlog p.210, SZ 161498): na
            # etoy zayavke "29 sobytiy, first 2025-06-11" chital osy kak "hronicheskiy defekt s
            # proshlogo goda", hotya realno bylo 3 sobytiya NA CHUZHOM zheleze + 26 na etoy
            # sborke - chuzhaya istoriya molcha vlivalas v odnu svodku.
            if ($split.Foreign.Count -gt 0) {
                "=== DO SBORKI (CHUZHOE ZHELEZO, {0} sobytiy do {1:yyyy-MM-dd HH:mm}) ===" -f $split.Foreign.Count, $SZ_HW_SINCE
                "istoriya DRUGOGO zheleza - v svodku etoy sborki NE vhodit."
                $ff = @($split.Foreign)
                "period: {0:dd.MM.yyyy} .. {1:dd.MM.yyyy}" -f `
                    ($ff | Sort-Object TimeCreated | Select-Object -First 1).TimeCreated, `
                    ($ff | Sort-Object TimeCreated -Descending | Select-Object -First 1).TimeCreated
                $ff | Group-Object { $_.TimeCreated.ToString('yyyy-MM-dd') } | Sort-Object Name |
                    ForEach-Object { "  {0}: {1}" -f $_.Name, $_.Count }
            }
            "=== NA ETOM ZHELEZE ==="
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

                "--- chastota otkazov na chas narabotki (NE na kalendarnyy den, p.132) ---"
                # 25 vyrubonov za 4 sutok kalendarya vygladit huzhe, chem 25 za ~26-30 chasov
                # realnoy narabotki (161346: mashina prospala v S3 pochti vse eto vremya).
                # Narabotka schitaetsya po SMART PowerOnHours - edinstvennaya velichina, kotoraya
                # ne rastet vo sne/gibernacii.
                try {
                    $poh = @(Get-NvmeSmartRows | Where-Object { -not $_.ReadError } |
                        ForEach-Object { [double]"$($_.PowerOnHours)" } | Where-Object { $_ -gt 0 })
                    if ($poh.Count -gt 0) {
                        $totalHours = ($poh | Measure-Object -Maximum).Maximum
                        if ($hard.Count -gt 0) {
                            "hard-off: {0} za {1:N0} ch narabotki (SMART PowerOnHours) = 1 na {2:N1} ch" -f `
                                $hard.Count, $totalHours, ($totalHours / $hard.Count)
                        } else {
                            "hard-off: 0 za {0:N0} ch narabotki (SMART PowerOnHours)" -f $totalHours
                        }
                    } else {
                        "narabotka (SMART PowerOnHours) nedostupna - chastotu na chas schitat ne iz chego."
                    }
                } catch { "narabotka (SMART PowerOnHours) nedostupna: $($_.Exception.Message)" }

                "--- last 20 events (details) ---"
                $parsed | Select-Object -First 20 | ForEach-Object {
                    "[{0}] {1} Bugcheck={2} PowerButtonTs={3} SleepInProgress={4}" -f `
                        $_.Time, $_.Kind, $_.Bug, $_.Pbt, $_.Sleep
                }
                if ($kp.Count -gt 20) { "... {0} earlier events not listed (see totals and histogram above)" -f ($kp.Count - 20) }
                "Podskazka: BugcheckCode=0, PowerButtonTs=0 i net BSOD/WHEA => zhestkiy obryv (pitanie/peregrev), a ne soft."
            } else { "Kernel-Power 41: 0 events na etom zheleze (net avariynyh vyrubonov v zhurnale)" }

            "=== NVMe Unsafe Shutdowns (nezavisimoe ot zhurnala OS podtverzhdenie, p.142) ==="
            # Zhurnal OS mozhno osporit kak sboy OS (na 161346 tak i sdelali); schetchik
            # nakopitelya - nezavisimyy istochnik. Rashozhdenie N i M samo po sebe informativno.
            try {
                $nvmeRows = @(Get-NvmeSmartRows | Where-Object { -not $_.ReadError })
                if ($nvmeRows.Count -eq 0) {
                    "NVMe diskov net (ili log 02h nedostupen)."
                } else {
                    $totalUnsafe = ($nvmeRows | ForEach-Object { [int64]"$($_.UnsafeShutdowns)" } | Measure-Object -Sum).Sum
                    "zhurnal OS (Kernel-Power 41): {0} sobytiy" -f $kp.Count
                    "schetchik nakopitelya (summa Unsafe Shutdowns po vsem NVMe): {0}" -f $totalUnsafe
                    $nvmeRows | Select-Object Disk, Serial, UnsafeShutdowns, PowerOnHours, PowerCycles | Format-Table -Auto | Out-String
                    "Podskazka: eto DVA NEZAVISIMYH istochnika - zhurnal OS mozhno osporit kak sboy OS, schetchik nakopitelya net."
                }
            } catch { "NVMe SMART nedostupen: $($_.Exception.Message)" }

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
            EventWindow.PowerShellPrologue() + TimeZoneNote.PowerShellPrologue() + CperDecoder.PowerShellPrologue() + HardwareWindow.PowerShellPrologue() + """
            Write-TzNote
            Write-JournalDepthNote
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

        // THERMTRIP (аппаратный термозащитный сброс) НЕ ЛОГИРУЕТСЯ В ПРИНЦИПЕ: питание
        // снимается в железе, ОС не получает ни прерывания, ни шанса на запись. На выходе
        // Kernel-Power 41 + BugcheckCode=0 + пустые дампы — ровно то же, что от просадки БП
        // или КЗ по +5В. На 160636 фильтр тротлинга без явного ProviderName поймал ЧУЖОЕ
        // событие с тем же Id (Microsoft-Windows-Time-Service) и дал ложный вывод «тротлинга
        // нет»; вдобавок 4.5ч OCCT на открытом стенде в прохладном сервисе не воспроизвели
        // дефект, который у клиента проявлялся за 1-15ч в закрытом корпусе (бэклог п.36b).
        Probe("thermal", "Тепловой профиль (тротлинг + распределение вырубонов по времени суток)",
            TimeZoneNote.PowerShellPrologue() + EventWindow.PowerShellPrologue() + HardwareWindow.PowerShellPrologue() + """
            Write-TzNote
            Write-JournalDepthNote
            "=== Okno etogo zheleza ==="
            Write-HwWindow

            "!!! THERMTRIP NE LOGIRUETSYA V PRINTSIPE: apparatnyy termozashchitnyy sbros snimaet"
            "pitanie v zheleze, OS ne poluchaet ni preryvaniya, ni shansa na zapis. Otsutstvie"
            "sobytiy nizhe NE ISKLYUCHAET teplovoy stsenariy - eto otvet 'net dannyh o trotlinge',"
            "a ne 'peregrev isklyuchen'."

            "=== Kernel-Processor-Power Id 37/86 (trotling, YAVNYY ProviderName) ==="
            # Filtr BEZ ProviderName lovit CHUZHIE sobytiya s tem zhe Id: na 160636 Id=37 bez
            # ProviderName dal Microsoft-Windows-Time-Service, i vyvod byl "trotlinga net" -
            # eto byla oshibka, a ne fakt (backlog p.36b, smezhno s p.31).
            $thr = @()
            try {
                $thr = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Processor-Power'; Id=37,86 } -ErrorAction Stop)
            } catch { }
            $thrSplit = Split-ByHwWindow $thr
            $thr = @($thrSplit.Ours)
            if ($thrSplit.Foreign.Count -gt 0) {
                "VNIMANIE: {0} sobytiy trotlinga otbrosheno kak istoriya DRUGOGO zheleza." -f $thrSplit.Foreign.Count
            }
            if ($thr.Count -gt 0) {
                "TOTAL: {0}, first {1:yyyy-MM-dd HH:mm:ss}, last {2:yyyy-MM-dd HH:mm:ss}" -f `
                    $thr.Count, $thr[-1].TimeCreated, $thr[0].TimeCreated
                $thr | Group-Object Id | ForEach-Object { "Id {0}: {1}" -f $_.Name, $_.Count }
            } else {
                "Kernel-Processor-Power 37/86: 0 (eto NE dokazatelstvo otsutstviya peregreva - sm. VNIMANIE pro THERMTRIP vyshe)"
            }

            "=== Raspredelenie hard-off (Kernel-Power 41) po vremeni sutok ==="
            # Kosvennyy priznak teplovogo stsenariya: vyrubony vecherom/nochyu posle chasov
            # raboty v zharkoy komnate chashche ukazyvayut na nakoplenie tepla v korpuse, chem
            # ravnomernoe raspredelenie po sutkam.
            $kp = @()
            try { $kp = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41 } -ErrorAction Stop) } catch { }
            $kpSplit = Split-ByHwWindow $kp
            $kp = @($kpSplit.Ours)
            if ($kp.Count -gt 0) {
                $buckets = $kp | Group-Object {
                    $h = $_.TimeCreated.Hour
                    if ($h -ge 6 -and $h -lt 12) { 'utro (06-12)' }
                    elseif ($h -ge 12 -and $h -lt 18) { 'den (12-18)' }
                    elseif ($h -ge 18 -and $h -lt 24) { 'vecher (18-24)' }
                    else { 'noch (00-06)' }
                }
                $buckets | Sort-Object Count -Descending | ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }
                $eveningOrNight = @($kp | Where-Object { $_.TimeCreated.Hour -ge 18 -or $_.TimeCreated.Hour -lt 6 }).Count
                if (($eveningOrNight / $kp.Count) -ge 0.66) {
                    "!!! Bolshinstvo hard-off prihoditsya na vecher/noch ({0} iz {1}) - kosvennyy" -f $eveningOrNight, $kp.Count
                    "priznak teplovogo stsenariya (nakoplenie tepla v zakrytom korpuse za den ekspluatatsii)."
                }
            } else {
                "Kernel-Power 41: 0 sobytiy - raspredelyat po vremeni sutok nechego."
            }

            "=== Delta hotspot-core ==="
            "Ne vychislyaetsya etoy probay: nuzhen pryamoy dostup k sensoram (LibreHardwareMonitor/"
            "lhmmon), kotorogo net cherez WMI/Get-WinEvent. Gonyat otdelnym zahodom lhmmon pod"
            "nagruzkoy (sm. tools/recipes) - eto ne 'net dannyh, znachit vsyo OK'."

            "=== Metodika teplovogo stsenariya ==="
            "Proveryat v SOBRANNOM korpuse, ne na otkrytom stende: na otkrytom stende greyutsya"
            "kristally, no ne vozduh vokrug korpusa - progon v prohladnom servise mozhet NE"
            "vosproizvesti defekt, kotoryy u klienta proyavlyaetsya za chasy raboty v zakrytom obieme."
            "Logirovat temperaturu vhodyashchego vozduha (datchik platy 'Temperature #1' cherez"
            "lhmmon) i sravnivat so stendom."
            "V voprosnik po zayavke - punkt 'gde stoit sistemnik' (nisha, shkaf,"
            "vplotnuyu k stene, batareya) - eto polovina diagnoza pri hard-off bez sledov."
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
                    # Report Id / "Identifikator otcheta" (RU) / etc - zagolovok zavisit ot
                    # yazyka Windows, a GUID-format - net. Lovim signaturu, a ne zagolovok.
                    $rid = ''
                    if ($e.Message -match '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})') { $rid = $matches[1] }
                    $dmp = ''
                    if ($e.Message -match '([A-Za-z0-9_.-]+\.dmp)') { $dmp = $matches[1] }
                    [PSCustomObject]@{ Time = $e.TimeCreated; P1 = $p1; Code = (Fmt-P1 $p1); ReportId = $rid; Dmp = $dmp }
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
                #
                # Talking to the GPU driver AT ALL produces its own events: on 161190 pairs
                # 0x117+0x1cc landed exactly on the minute of OUR OWN gpu-idle-state.ps1 probe,
                # plus on boot and session logon - polling "does it fire while idle" measured
                # the tool itself, not the machine (backlog p.219). Mark events near boot/logon
                # so they are not offered as a symptom.
                $osInfo = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
                $logons = @()
                try {
                    $logons = @(Get-WinEvent -FilterHashtable @{ LogName='Security'; Id=4624 } -ErrorAction Stop |
                        Select-Object -ExpandProperty TimeCreated)
                } catch { }   # Security log may need audit policy / elevated rights - absence is fine

                "--- gruppy po vremeni (pachka = >=3 sobytiy v odnu sekundu) ---"
                $groups = $evts | Group-Object { $_.Time.ToString('yyyy-MM-dd HH:mm:ss') } | Sort-Object Name -Descending
                $withDump = 0; $ownActivity = 0; $artifacts = 0
                $shown = 0
                foreach ($g in $groups) {
                    $t = [datetime]::ParseExact($g.Name, 'yyyy-MM-dd HH:mm:ss', $null)
                    # A dump written within +-2 minutes marks a REAL event.
                    $near = @($lk | Where-Object { [math]::Abs(($_.LastWriteTime - $t).TotalSeconds) -le 120 })
                    $real = $near.Count -gt 0
                    $bootNear = $osInfo -and $osInfo.LastBootUpTime -and ([math]::Abs(($osInfo.LastBootUpTime - $t).TotalSeconds) -le 120)
                    $logonNear = @($logons | Where-Object { [math]::Abs(($_ - $t).TotalSeconds) -le 120 })
                    if ($real) { $withDump += $g.Count }
                    elseif ($bootNear -or $logonNear.Count -gt 0) { $ownActivity += $g.Count }
                    else { $artifacts += $g.Count }
                    if ($shown -lt 25) {
                        $mark = if ($real) { "NASTOYASHEE (ryadom damp: {0})" -f $near[0].Name }
                                elseif ($bootNear) { 'sovpadaet s zagruzkoy sistemy - NE simptom, sledstvie starta drayverov' }
                                elseif ($logonNear.Count -gt 0) { 'sovpadaet so vhodom v sessiyu (logon) - NE simptom' }
                                elseif ($g.Count -ge 3) { 'pachka bez dampa - veroyatno artefakt zakrytiya 3D-prilozheniya (stress-test)' }
                                else { 'bez dampa' }
                        "{0} x{1} [{2}] {3}" -f $g.Name, $g.Count, (($g.Group | Select-Object -First 1).Code), $mark
                        $shown++
                    }
                }
                if ($groups.Count -gt 25) { "... esche {0} grupp ne pokazano" -f ($groups.Count - 25) }
                "ITOGO: sobytiy s dampom {0}, sovpadenie s zagruzkoy/logonom {1}, veroyatnyh artefaktov {2} (iz {3})" -f `
                    $withDump, $ownActivity, $artifacts, $evts.Count
                if (($withDump + $ownActivity) -eq 0 -and $evts.Count -gt 0) {
                    "VNIMANIE: ni odno sobytie ne podtverzhdeno dampom - schitat 'videopodsistema sypetsya' po etim cifram NELZYA (p.94)."
                }

                # Glavnaya oshibka na 161211 (p.199): 8572 sobytiya prochitali kak "8572 raza
                # slomalos", hotya WER beskonechno retraint ochered ReportQueue - odin real'nyy
                # incident daet desyatki povtorov odnogo i togo zhe otcheta. Schitat nado
                # UNIKALNYE otchety (Report Id), a ne stroki zhurnala.
                "--- UNIKALNYE OTCHETY (Report Id, a ne stroki zhurnala - WER retraint ochered) ---"
                $withId = @($evts | Where-Object { $_.ReportId })
                if ($withId.Count -gt 0) {
                    $reports = @($withId | Group-Object ReportId | ForEach-Object {
                        $g = $_.Group | Sort-Object Time
                        [PSCustomObject]@{ ReportId = $_.Name; Code = $g[0].Code; First = $g[0].Time; EventCount = $_.Count }
                    })
                    "vsego unikalnyh otchetov: {0} (iz {1} sobytiy zhurnala)" -f $reports.Count, $evts.Count
                    $reports | Group-Object Code | Sort-Object Count -Descending | ForEach-Object {
                        $evCount = ($_.Group | Measure-Object EventCount -Sum).Sum
                        "{0}: {1} incidentov ({2} sobytiy - eto retrai WER, ne novye sobytiya)" -f $_.Name, $_.Count, $evCount
                    }
                    if ($reports.Count -lt $evts.Count) {
                        "VAZHNO: {0} sobytiy zhurnala - eto vsego {1} unikalnyh incidentov; sudit o chastote defekta po SOBYTIYAM (a ne otchetam) NELZYA." -f $evts.Count, $reports.Count
                    }
                } else {
                    "Report Id ne izvlechen iz Message - schet ostaetsya po sobytiyam zhurnala (nizhe)."
                }

                $queueCount = @(Get-ChildItem 'C:\ProgramData\Microsoft\Windows\WER\ReportQueue' -Directory -ErrorAction SilentlyContinue).Count
                "razmer ocheredi WER (ReportQueue): {0} papok - bolshaya ochered sama po sebe obyasnyaet tysyachi sobytiy-retraev." -f $queueCount

                if ($lk.Count -gt 0) {
                    $lastReal = ($lk | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
                    $daysAgo = [math]::Floor(((Get-Date) - $lastReal).TotalDays)
                    "POSLEDNIJ REALNYJ INCIDENT (data fayla dampa v LiveKernelReports): {0:yyyy-MM-dd}, {1} dney nazad." -f $lastReal, $daysAgo
                    if ($daysAgo -ge 1) {
                        "Eto NE 'sypetsya prjamo seychas' - realnyh dampov za poslednie {0} dney net, dazhe esli sobytiy WER v zhurnale mnogo." -f $daysAgo
                    }
                } else {
                    "POSLEDNIJ REALNYJ INCIDENT: faylov dampov v LiveKernelReports net (sm. sektsiyu vyshe)."
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

            "=== Sverka LiveKernelEvent s Kernel-Power 41 (b.191, SZ 161556) ==="
            # 35 x Kernel-Power 41 i NOL BugCheck 1001/WHEA v zhurnale System - "prichiny net" po
            # zhurnalu. Nastoyashaya prichina - 9 x LiveKernelEvent 0x141 (VIDEO_ENGINE_TIMEOUT),
            # kazhdyi za minutu do vyrubona. Bez etoy sverki svyaz vidna tolko ruchnym sopostavleniem.
            try {
                $kp41 = @(Get-WinEvent -FilterHashtable @{ LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41 } -ErrorAction Stop |
                    Select-Object -ExpandProperty TimeCreated)
            } catch { $kp41 = @() }
            $lkEvts = @($evts | Where-Object { $_ })
            if ($lkEvts.Count -eq 0 -or $kp41.Count -eq 0) {
                "LiveKernelEvent net ili Kernel-Power 41 net - sverka nevozmozhna."
            } else {
                $matched = 0
                foreach ($e in ($lkEvts | Sort-Object Time)) {
                    $near = $kp41 | Where-Object { $_ -ge $e.Time -and ($_ - $e.Time).TotalMinutes -le 5 } |
                        Sort-Object | Select-Object -First 1
                    if ($near) {
                        $matched++
                        "{0:yyyy-MM-dd HH:mm:ss} {1} -> vyrubon (KP41) cherez {2:N1} min" -f `
                            $e.Time, $e.Code, ($near - $e.Time).TotalMinutes
                    }
                }
                if ($matched -eq 0) { "Ni odno LiveKernelEvent ne sovpadaet s KP41 v okne 5 minut." }
                else { "ITOGO: {0} iz {1} LiveKernelEvent predshestvuyut vyrubonu KP41 v okne 5 min." -f $matched, $lkEvts.Count }
            }
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
                $k = $kind[[int]$cc.CrashDumpEnabled]
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

        // RGB/HID kontrollery podsvetki: prinyatie proshivki lyubogo takogo kontrollera
        // (bootloader -> normalnyy rezhim) trebuet Product string i caps s ustroystva, a ne
        // tolko FriendlyName iz PnP - u 'ITE Upgrade Mode(128)' i 'GIGABYTE Device' odinakovyy
        // Class=HIDClass, i tolko VID:PID + Input/Output report length otlichayut bootloader
        // ot proshitogo kontrollera (backlog, SZ 163013). x64-only P/Invoke: agent - odin
        // self-contained win-x64 build, x86 SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize ne nuzhen.
        Probe("rgb", "RGB/HID-контроллеры (Product string + caps для приёмки прошивки)", """
            $sig = @'
            using System;
            using System.Collections.Generic;
            using System.Runtime.InteropServices;
            using System.Text;

            public class SzDiagHid {
                public const int DIGCF_PRESENT = 0x02;
                public const int DIGCF_DEVICEINTERFACE = 0x10;
                public const uint FILE_SHARE_READ = 0x01;
                public const uint FILE_SHARE_WRITE = 0x02;
                public const uint OPEN_EXISTING = 3;
                public const int HIDP_STATUS_SUCCESS = 0x00110000;

                [StructLayout(LayoutKind.Sequential)]
                public struct SP_DEVICE_INTERFACE_DATA {
                    public int cbSize;
                    public Guid InterfaceClassGuid;
                    public int Flags;
                    public IntPtr Reserved;
                }

                [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
                public struct SP_DEVICE_INTERFACE_DETAIL_DATA {
                    public int cbSize;
                    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
                    public string DevicePath;
                }

                [DllImport("hid.dll")]
                public static extern void HidD_GetHidGuid(out Guid hidGuid);

                [DllImport("setupapi.dll", SetLastError = true)]
                public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

                [DllImport("setupapi.dll", SetLastError = true)]
                public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
                    ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

                [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
                public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
                    ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, ref SP_DEVICE_INTERFACE_DETAIL_DATA deviceInterfaceDetailData,
                    int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

                [DllImport("setupapi.dll")]
                public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

                [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
                public static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
                    IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

                [DllImport("kernel32.dll")]
                public static extern bool CloseHandle(IntPtr handle);

                [StructLayout(LayoutKind.Sequential)]
                public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

                [DllImport("hid.dll")]
                public static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

                [DllImport("hid.dll")]
                public static extern bool HidD_GetProductString(IntPtr hidDeviceObject, byte[] buffer, int bufferLength);

                [DllImport("hid.dll")]
                public static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

                [DllImport("hid.dll")]
                public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

                [StructLayout(LayoutKind.Sequential)]
                public struct HIDP_CAPS {
                    public ushort Usage;
                    public ushort UsagePage;
                    public ushort InputReportByteLength;
                    public ushort OutputReportByteLength;
                    public ushort FeatureReportByteLength;
                    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
                    public ushort NumberLinkCollectionNodes;
                    public ushort NumberInputButtonCaps;
                    public ushort NumberInputValueCaps;
                    public ushort NumberInputDataIndices;
                    public ushort NumberOutputButtonCaps;
                    public ushort NumberOutputValueCaps;
                    public ushort NumberOutputDataIndices;
                    public ushort NumberFeatureButtonCaps;
                    public ushort NumberFeatureValueCaps;
                    public ushort NumberFeatureDataIndices;
                }

                [DllImport("hid.dll")]
                public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS caps);

                public static List<string> EnumerateDevicePaths() {
                    var result = new List<string>();
                    Guid hidGuid;
                    HidD_GetHidGuid(out hidGuid);
                    IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
                    if (set == IntPtr.Zero) return result;
                    try {
                        uint index = 0;
                        while (true) {
                            var ifData = new SP_DEVICE_INTERFACE_DATA();
                            ifData.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref ifData)) break;
                            var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                            detail.cbSize = 8;   // x64-only: agent - odin self-contained win-x64 build
                            int required;
                            if (SetupDiGetDeviceInterfaceDetail(set, ref ifData, ref detail, Marshal.SizeOf(detail), out required, IntPtr.Zero)) {
                                result.Add(detail.DevicePath);
                            }
                            index++;
                        }
                    } finally { SetupDiDestroyDeviceInfoList(set); }
                    return result;
                }

                public static string Describe(string devicePath) {
                    IntPtr handle = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (handle == new IntPtr(-1)) return devicePath + " : CreateFile failed";
                    try {
                        var attrs = new HIDD_ATTRIBUTES();
                        attrs.Size = Marshal.SizeOf(attrs);
                        HidD_GetAttributes(handle, ref attrs);

                        var buf = new byte[256];
                        string product = "";
                        if (HidD_GetProductString(handle, buf, buf.Length)) {
                            product = Encoding.Unicode.GetString(buf);
                            int z = product.IndexOf('\0');
                            if (z >= 0) product = product.Substring(0, z);
                        }

                        string caps = "n/a";
                        IntPtr preparsed;
                        if (HidD_GetPreparsedData(handle, out preparsed)) {
                            try {
                                HIDP_CAPS c;
                                if (HidP_GetCaps(preparsed, out c) == HIDP_STATUS_SUCCESS) {
                                    caps = "UsagePage=" + c.UsagePage + " Usage=" + c.Usage +
                                           " Input=" + c.InputReportByteLength + " Output=" + c.OutputReportByteLength +
                                           " Feature=" + c.FeatureReportByteLength;
                                }
                            } finally { HidD_FreePreparsedData(preparsed); }
                        }

                        return "VID_" + attrs.VendorID.ToString("X4") + "&PID_" + attrs.ProductID.ToString("X4") +
                               " Product='" + product + "' " + caps;
                    } finally { CloseHandle(handle); }
                }
            }
            '@
            try {
                Add-Type -TypeDefinition $sig -ErrorAction Stop

                "=== HID (nizkiy uroven: VID:PID, Product string, caps) ==="
                $paths = [SzDiagHid]::EnumerateDevicePaths()
                if ($paths.Count -eq 0) { "HID-ustroystv ne naydeno." }
                foreach ($p in $paths) {
                    try { [SzDiagHid]::Describe($p) } catch { $p + " : " + $_.Exception.Message }
                }
            } catch {
                "HID low-level probe unavailable: $($_.Exception.Message)"
            }

            "=== HID (PnP, dlya sopostavleniya s FriendlyName) ==="
            Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object Status -eq 'OK' |
                Select-Object FriendlyName, InstanceId | Format-Table -Auto | Out-String
            """),
    };
}
