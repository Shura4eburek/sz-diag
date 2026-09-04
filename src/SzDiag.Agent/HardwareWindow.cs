namespace SzDiag.Agent;

/// <summary>С какого момента история журналов относится к ЭТОМУ железу.
///
/// Боль (бэклог п.92, СЗ 161432): машину гоняли под тестовой Windows с внешнего USB-SSD —
/// образ сервиса, кочующий между машинами. `diag` честно выдал 79 Kernel-Power 41 с прошлого
/// октября и 12 `Cache Hierarchy Error` строго на APIC 6/7 («приборный подпис дохлого проца»),
/// хотя всё это следы **других машин**: релевантно только окно после последней загрузки.
/// Спасло лишь то, что оператор сам сказал «это тестовая винда».
///
/// Вторая боль (бэклог п.210, СЗ 161498): первая версия брала <c>DEVPKEY_Device_InstallDate</c>
/// (едет при переустановке драйвера) и моду по дню среди ВСЕХ PCI-устройств — на машине с
/// перенесённой ОС это дало границу на ГОД раньше реальной, и в сводку вырубонов подмешалась
/// история чужого компьютера клиента. Правильный свидетель —
/// <c>DEVPKEY_Device_FirstInstallDate</c> (не едет при переустановках) только у НЕСЪЁМНЫХ
/// ключевых устройств платформы: сетевые контроллеры/шины, GPU, системный диск (по BusType,
/// чтобы отсечь съёмные USB-флешки) — момент, когда ЭТА система впервые увидела ЭТОТ
/// экземпляр железа.</summary>
public static class HardwareWindow
{
    /// <summary>PowerShell-пролог: считает <c>$SZ_HW_SINCE</c> (дата или <c>$null</c>) и
    /// печатает шапку про переносную ОС. Строго ASCII — тела проб уходят на клиента через
    /// EncodedCommand и читаются PowerShell 5.1.</summary>
    public static string PowerShellPrologue() => """
        # When did THIS OS first boot on THIS hardware (backlog p.92/p.210)?
        # FirstInstallDate (unlike InstallDate) does not move when a driver is reinstalled -
        # it is the moment THIS system first saw THIS exact device instance. Only key
        # non-removable platform devices count: network controllers/buses, GPU, the system
        # disk (checked by BusType so a USB flash drive cannot masquerade as "birth date").
        function Get-HwSinceDevices {
            $found = @()
            try {
                foreach ($d in (Get-PnpDevice -PresentOnly -ErrorAction Stop |
                        Where-Object { $_.InstanceId -like 'PCI\*' -and $_.Class -in @('Net','System','SCSIAdapter','HDC','Display') })) {
                    $p = Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_FirstInstallDate' -ErrorAction SilentlyContinue
                    if ($p -and $p.Data) {
                        $found += [PSCustomObject]@{ Name = $d.FriendlyName; Class = $d.Class; Date = [datetime]$p.Data }
                    }
                }
            } catch { }
            try {
                $sysDisk = Get-Partition -DriveLetter ($env:SystemDrive.TrimEnd(':')) -ErrorAction Stop |
                    Get-Disk -ErrorAction Stop
                $phys = Get-PhysicalDisk -ErrorAction Stop | Where-Object DeviceId -eq $sysDisk.Number
                # A USB-attached system disk (rare: PE/live-USB Windows) is removable by
                # definition - its "first seen" date is when the stick was plugged in, not
                # when the machine was built. Only trust non-USB buses.
                if ($phys -and $phys.BusType -ne 'USB') {
                    $dd = Get-CimInstance Win32_DiskDrive -ErrorAction Stop | Where-Object Index -eq $sysDisk.Number
                    if ($dd -and $dd.PNPDeviceID) {
                        $p = Get-PnpDeviceProperty -InstanceId $dd.PNPDeviceID -KeyName 'DEVPKEY_Device_FirstInstallDate' -ErrorAction SilentlyContinue
                        if ($p -and $p.Data) {
                            $found += [PSCustomObject]@{ Name = "sistemnyy disk: $($dd.Model)"; Class = 'DiskDrive'; Date = [datetime]$p.Data }
                        }
                    }
                }
            } catch { }
            return $found
        }

        # Median (odd count) or the lower of the two middle values (even count) - robust to
        # a single device reporting a stale FirstInstallDate (e.g. after RMA re-pairing).
        function Get-MedianDate($devices) {
            $sorted = @($devices | Sort-Object Date)
            if ($sorted.Count -eq 0) { return $null }
            return $sorted[[math]::Floor(($sorted.Count - 1) / 2)].Date
        }

        $SZ_OS_INSTALL = $null
        try { $SZ_OS_INSTALL = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).InstallDate } catch { }
        $SZ_HW_SINCE = $null
        $SZ_HW_DEVICES = @()
        $SZ_PORTABLE = $false
        # Too few key devices found - no verdict. Silence beats a wrong cut-off date.
        $devs = @(Get-HwSinceDevices)
        $hw = $null
        if ($devs.Count -ge 2) { $hw = Get-MedianDate $devs }
        elseif ($devs.Count -eq 1) { $hw = $devs[0].Date }
        if ($hw -and $SZ_OS_INSTALL -and $hw -gt $SZ_OS_INSTALL.AddDays(1)) {
            $SZ_HW_SINCE = $hw
            $SZ_HW_DEVICES = $devs
            $SZ_PORTABLE = $true
        }

        function Write-HwWindow {
            if ($SZ_PORTABLE) {
                "!!! OS PERENOSNAYA (ili menyalos zhelezo): ustanovlena {0:yyyy-MM-dd}, a na ETOY mashine s {1:yyyy-MM-dd HH:mm}." -f $SZ_OS_INSTALL, $SZ_HW_SINCE
                "    Sobytiya DO {0:yyyy-MM-dd HH:mm} otnosyatsya k DRUGOMU zhelezu i v svodki ne vklyuchayutsya." -f $SZ_HW_SINCE
                "    Granitsa vzyata po ustroystvam ({0} sht):" -f $SZ_HW_DEVICES.Count
                foreach ($d in $SZ_HW_DEVICES) { "      {0:yyyy-MM-dd HH:mm:ss} [{1}] {2}" -f $d.Date, $d.Class, $d.Name }
            } elseif ($hw) {
                "OS na etom zheleze s {0:yyyy-MM-dd HH:mm} (ustanovlena {1:yyyy-MM-dd})." -f $hw, $SZ_OS_INSTALL
            } else {
                "Opredelit, s kakogo momenta istoriya otnositsya k etomu zhelezu, ne udalos - schitat vsyu istoriyu svoey NELZYA bez proverki."
            }
        }

        # Splits events into 'ours' and 'from other hardware'. Without $SZ_HW_SINCE everything
        # stays 'ours' - we do not invent a cut-off we cannot prove.
        function Split-ByHwWindow($events) {
            if (-not $SZ_HW_SINCE) { return [PSCustomObject]@{ Ours = @($events); Foreign = @() } }
            [PSCustomObject]@{
                Ours    = @($events | Where-Object { $_.TimeCreated -ge $SZ_HW_SINCE })
                Foreign = @($events | Where-Object { $_.TimeCreated -lt $SZ_HW_SINCE })
            }
        }
        """;
}
