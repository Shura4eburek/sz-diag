namespace SzDiag.Agent;

/// <summary>NVMe SMART (log page 02h) — единая точка чтения счётчиков для секций <c>storage</c>
/// и <c>reboots</c>. <c>Get-StorageReliabilityCounter</c> отдаёт NVMe пустым (PowerOnHours/
/// ошибки/Unsafe Shutdowns), а именно Unsafe Shutdowns был главным доказательством отказа в
/// претензии на 160705 (67 при 178 циклах включения, против 73 Kernel-Power 41 в журнале —
/// два независимых источника подтверждают одно и то же, бэклог п.142). Читаем напрямую через
/// <c>IOCTL_STORAGE_QUERY_PROPERTY</c> / <c>StorageDeviceProtocolSpecificProperty</c>.
///
/// Строго ASCII: тела проб уходят на клиента через EncodedCommand и читаются PowerShell 5.1.</summary>
public static class NvmeSmart
{
    /// <summary>PowerShell-пролог: <c>Get-NvmeSmartRows</c> возвращает по одной строке на
    /// каждый физический NVMe-диск (или строку с полем <c>ReadError</c>, если лог не читается).</summary>
    public static string PowerShellPrologue() => """
        Add-Type -ErrorAction SilentlyContinue -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class NvmeLog
        {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec,
                uint disp, uint flags, IntPtr tmpl);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize,
                byte[] outBuf, int outSize, out int returned, IntPtr ov);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool CloseHandle(IntPtr h);

            const uint IOCTL = 0x2D1400;              // IOCTL_STORAGE_QUERY_PROPERTY
            const int PropertyId = 50;                // StorageDeviceProtocolSpecificProperty
            const int ProtoNvme = 3, DataTypeLogPage = 2, SmartLogPage = 2;
            const int HeaderSize = 8, SpecificSize = 40, LogSize = 512;

            public static byte[] Read(int driveNumber)
            {
                IntPtr h = CreateFileW(@"\\.\PhysicalDrive" + driveNumber, 0,
                    3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h == new IntPtr(-1))
                    throw new Exception("CreateFile PhysicalDrive" + driveNumber + " failed, win32=" + Marshal.GetLastWin32Error());
                try
                {
                    int total = HeaderSize + SpecificSize + LogSize;
                    byte[] buf = new byte[total];
                    BitConverter.GetBytes(PropertyId).CopyTo(buf, 0);
                    BitConverter.GetBytes(0).CopyTo(buf, 4);
                    BitConverter.GetBytes(ProtoNvme).CopyTo(buf, 8);
                    BitConverter.GetBytes(DataTypeLogPage).CopyTo(buf, 12);
                    BitConverter.GetBytes(SmartLogPage).CopyTo(buf, 16);
                    BitConverter.GetBytes(0).CopyTo(buf, 20);
                    BitConverter.GetBytes(SpecificSize).CopyTo(buf, 24);
                    BitConverter.GetBytes(LogSize).CopyTo(buf, 28);

                    byte[] outBuf = new byte[total];
                    int ret;
                    if (!DeviceIoControl(h, IOCTL, buf, total, outBuf, total, out ret, IntPtr.Zero))
                        throw new Exception("DeviceIoControl failed, win32=" + Marshal.GetLastWin32Error());

                    byte[] log = new byte[LogSize];
                    Array.Copy(outBuf, HeaderSize + SpecificSize, log, 0, LogSize);
                    return log;
                }
                finally { CloseHandle(h); }
            }
        }
        '@
        function Get-U128 { param([byte[]]$Log, [int]$Offset)
            $bytes = New-Object byte[] 17
            [Array]::Copy($Log, $Offset, $bytes, 0, 16)
            [System.Numerics.BigInteger]::new($bytes)
        }

        function Get-NvmeSmartRows {
            $rows = @()
            $nvme = @(Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object BusType -eq 'NVMe' | Sort-Object DeviceId)
            foreach ($d in $nvme) {
                $num = [int]$d.DeviceId
                try { $log = [NvmeLog]::Read($num) }
                catch {
                    $rows += [PSCustomObject]@{
                        Disk = $d.FriendlyName; Serial = $d.SerialNumber
                        ReadError = "PhysicalDrive{0} ({1}): oshibka chteniya loga - {2}" -f $num, $d.FriendlyName, $_.Exception.Message
                    }
                    continue
                }

                $crit = $log[0]
                $warn = @()
                if ($crit -band 0x01) { $warn += 'spare below threshold' }
                if ($crit -band 0x02) { $warn += 'temperature threshold exceeded' }
                if ($crit -band 0x04) { $warn += 'NVM subsystem reliability degraded' }
                if ($crit -band 0x08) { $warn += 'media in read-only mode' }
                if ($crit -band 0x10) { $warn += 'volatile memory backup failed' }
                $media = Get-U128 $log 160
                # Verdikt po polyam NVMe, a ne 'OK' na pustyh schetchikah (p.120).
                $verdict = if ($crit -ne 0 -or $media -gt 0) { 'SUSPECT' } else { 'OK po logu 02h' }
                $rows += [PSCustomObject]@{
                    Disk               = $d.FriendlyName
                    Serial             = $d.SerialNumber
                    CriticalWarning    = if ($warn.Count -eq 0) { 'net (0x00)' } else { ('0x{0:X2}: {1}' -f $crit, ($warn -join ', ')) }
                    TempC              = $(if (($t = [BitConverter]::ToUInt16($log, 1)) -gt 0) { $t - 273 } else { 0 })
                    PercentageUsed     = "$($log[5]) %"
                    AvailableSpare     = "$($log[3]) % (porog $($log[4]) %)"
                    DataUnitsRead_TB   = [math]::Round([double](Get-U128 $log 32) * 512000 / 1TB, 2)
                    DataUnitsWritten_TB= [math]::Round([double](Get-U128 $log 48) * 512000 / 1TB, 2)
                    PowerCycles        = (Get-U128 $log 112).ToString()
                    PowerOnHours       = (Get-U128 $log 128).ToString()
                    UnsafeShutdowns    = (Get-U128 $log 144).ToString()
                    MediaErrors        = $media.ToString()
                    ErrorLogEntries    = (Get-U128 $log 176).ToString()
                    VERDICT            = $verdict
                }
            }
            $rows
        }
        """;
}
