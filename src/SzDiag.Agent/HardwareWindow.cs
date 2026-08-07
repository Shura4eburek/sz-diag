namespace SzDiag.Agent;

/// <summary>С какого момента история журналов относится к ЭТОМУ железу.
///
/// Боль (бэклог п.92, СЗ 161432): машину гоняли под тестовой Windows с внешнего USB-SSD —
/// образ сервиса, кочующий между машинами. `diag` честно выдал 79 Kernel-Power 41 с прошлого
/// октября и 12 `Cache Hierarchy Error` строго на APIC 6/7 («приборный подпис дохлого проца»),
/// хотя всё это следы **других машин**: релевантно только окно после последней загрузки.
/// Спасло лишь то, что оператор сам сказал «это тестовая винда».
///
/// Дискриминатор дешёвый и не требует состояния: у PCI-устройств есть дата установки
/// (<c>DEVPKEY_Device_InstallDate</c>) — при первой загрузке ОС на новом железе Windows
/// ставит драйверы всей платы разом, и мода этих дат по дню и есть «когда эта ОС впервые
/// увидела эту машину». Если она заметно позже установки ОС — образ переносной.</summary>
public static class HardwareWindow
{
    /// <summary>PowerShell-пролог: считает <c>$SZ_HW_SINCE</c> (дата или <c>$null</c>) и
    /// печатает шапку про переносную ОС. Строго ASCII — тела проб уходят на клиента через
    /// EncodedCommand и читаются PowerShell 5.1.</summary>
    public static string PowerShellPrologue() => """
        # When did THIS OS first boot on THIS hardware (backlog p.92)?
        # PCI device install dates: moving a disk to another machine installs the whole
        # platform's drivers at once, so the mode of those dates marks the first boot here.
        function Get-HwSince {
            $dates = @()
            try {
                foreach ($d in (Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object { $_.InstanceId -like 'PCI\*' })) {
                    $p = Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_InstallDate' -ErrorAction SilentlyContinue
                    if ($p -and $p.Data) { $dates += [datetime]$p.Data }
                }
            } catch { }
            # Too few devices - no verdict. Silence beats a wrong cut-off date.
            if ($dates.Count -lt 5) { return $null }
            $g = $dates | Group-Object { $_.ToString('yyyy-MM-dd') } | Sort-Object Count -Descending | Select-Object -First 1
            if ($g.Count -lt 3) { return $null }
            return ($g.Group | Sort-Object | Select-Object -First 1)
        }

        $SZ_OS_INSTALL = $null
        try { $SZ_OS_INSTALL = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).InstallDate } catch { }
        $SZ_HW_SINCE = $null
        $SZ_PORTABLE = $false
        $hw = Get-HwSince
        if ($hw -and $SZ_OS_INSTALL -and $hw -gt $SZ_OS_INSTALL.AddDays(1)) {
            $SZ_HW_SINCE = $hw
            $SZ_PORTABLE = $true
        }

        function Write-HwWindow {
            if ($SZ_PORTABLE) {
                "!!! OS PERENOSNAYA (ili menyalos zhelezo): ustanovlena {0:yyyy-MM-dd}, a na ETOY mashine s {1:yyyy-MM-dd HH:mm}." -f $SZ_OS_INSTALL, $SZ_HW_SINCE
                "    Sobytiya DO {0:yyyy-MM-dd HH:mm} otnosyatsya k DRUGOMU zhelezu i v svodki ne vklyuchayutsya." -f $SZ_HW_SINCE
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
