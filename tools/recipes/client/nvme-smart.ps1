$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# NVMe SMART / Health Information Log (page 02h) напрямую через IOCTL_STORAGE_QUERY_PROPERTY.
#
# Грабля (СЗ 161346): клиент просил зафиксировать Unsafe Shutdowns, а штатная секция
# `diag storage` (Get-StorageReliabilityCounter) на NVMe отдаёт только TempC/Wear —
# PowerOnHours, счётчики ошибок и Unsafe Shutdowns приходят ПУСТЫМИ. smartctl на хост
# не поставлен, тащить его на клиента ради двух чисел смысла нет: нужный лог отдаёт
# сама Windows через StorageDeviceProtocolSpecificProperty.
#
# Считает: Unsafe Shutdowns, Power Cycles, Power On Hours, Media Errors, Percentage Used,
# Critical Warning, Data Units R/W, температуру. Всё — 128-битные LE-счётчики.

Add-Type -TypeDefinition @'
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

    // IOCTL_STORAGE_QUERY_PROPERTY
    const uint IOCTL = 0x2D1400;
    // StorageDeviceProtocolSpecificProperty = 50, PropertyStandardQuery = 0
    const int PropertyId = 50;
    // ProtocolTypeNvme = 3, NVMeDataTypeLogPage = 2, SMART/Health log = 0x02
    const int ProtoNvme = 3, DataTypeLogPage = 2, SmartLogPage = 2;

    // STORAGE_PROPERTY_QUERY: 8 байт шапки + STORAGE_PROTOCOL_SPECIFIC_DATA (40) + сам лог (512).
    const int HeaderSize = 8, SpecificSize = 40, LogSize = 512;

    public static byte[] Read(int driveNumber)
    {
        // dwDesiredAccess = 0: только запрос свойств, без чтения секторов диска.
        IntPtr h = CreateFileW(@"\\.\PhysicalDrive" + driveNumber, 0,
            3 /* FILE_SHARE_READ|WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h == new IntPtr(-1))
            throw new Exception("CreateFile PhysicalDrive" + driveNumber + " failed, win32=" + Marshal.GetLastWin32Error());
        try
        {
            int total = HeaderSize + SpecificSize + LogSize;
            byte[] buf = new byte[total];
            BitConverter.GetBytes(PropertyId).CopyTo(buf, 0);
            BitConverter.GetBytes(0).CopyTo(buf, 4);              // QueryType = standard
            BitConverter.GetBytes(ProtoNvme).CopyTo(buf, 8);
            BitConverter.GetBytes(DataTypeLogPage).CopyTo(buf, 12);
            BitConverter.GetBytes(SmartLogPage).CopyTo(buf, 16);  // ProtocolDataRequestValue
            BitConverter.GetBytes(0).CopyTo(buf, 20);             // ProtocolDataRequestSubValue
            // ProtocolDataOffset считается от начала STORAGE_PROTOCOL_SPECIFIC_DATA, а не буфера.
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
'@ -ErrorAction Stop

# Счётчики в логе 128-битные; в decimal через BigInteger, чтобы не переполнить UInt64.
function Get-U128 { param([byte[]]$Log, [int]$Offset)
    $bytes = New-Object byte[] 17          # +1 нулевой старший — иначе BigInteger видит знак
    [Array]::Copy($Log, $Offset, $bytes, 0, 16)
    [System.Numerics.BigInteger]::new($bytes)
}

$disks = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object BusType -eq 'NVMe' | Sort-Object DeviceId
if (-not $disks) { "NVMe diskov ne naydeno."; return }

foreach ($d in $disks) {
    $num = [int]$d.DeviceId
    "=== PhysicalDrive$num : $($d.FriendlyName) ($([math]::Round($d.Size/1GB)) GB) ==="
    try {
        $log = [NvmeLog]::Read($num)
    } catch {
        "  OSHIBKA chteniya NVMe SMART log: $($_.Exception.Message)"
        ""
        continue
    }

    $crit   = $log[0]
    $tempK  = [BitConverter]::ToUInt16($log, 1)
    $spare  = $log[3]
    $spareT = $log[4]
    $used   = $log[5]

    # Critical Warning — битовая маска; словами, иначе '0x04' в отчёте ничего не значит.
    $warn = @()
    if ($crit -band 0x01) { $warn += 'spare below threshold' }
    if ($crit -band 0x02) { $warn += 'temperature threshold exceeded' }
    if ($crit -band 0x04) { $warn += 'NVM subsystem reliability degraded' }
    if ($crit -band 0x08) { $warn += 'media in read-only mode' }
    if ($crit -band 0x10) { $warn += 'volatile memory backup failed' }
    $warnText = if ($warn.Count -eq 0) { 'net (0x00)' } else { ('0x{0:X2}: {1}' -f $crit, ($warn -join ', ')) }

    [PSCustomObject]@{
        Disk               = $d.FriendlyName
        Serial             = $d.SerialNumber
        Firmware           = $d.FirmwareVersion
        CriticalWarning    = $warnText
        TempC              = if ($tempK -gt 0) { $tempK - 273 } else { 0 }
        PercentageUsed     = "$used %"
        AvailableSpare     = "$spare % (porog $spareT %)"
        DataUnitsRead_TB   = [math]::Round([double](Get-U128 $log 32) * 512000 / 1TB, 2)
        DataUnitsWritten_TB= [math]::Round([double](Get-U128 $log 48) * 512000 / 1TB, 2)
        PowerCycles        = (Get-U128 $log 112).ToString()
        PowerOnHours       = (Get-U128 $log 128).ToString()
        UnsafeShutdowns    = (Get-U128 $log 144).ToString()
        MediaErrors        = (Get-U128 $log 160).ToString()
        ErrorLogEntries    = (Get-U128 $log 176).ToString()
    } | Format-List | Out-String
}
