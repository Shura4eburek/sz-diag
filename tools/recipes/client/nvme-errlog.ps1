$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# NVMe Error Information Log (page 01h) + карта "PhysicalDrive N -> SCSI-port -> контроллер".
#
# Грабля (СЗ 161346): SMART (page 02h) показал у системного SSD ErrorLogEntries=20 при
# MediaErrors=0, а Windows при этом сыпала `disk 7 bad block` на ДРУГОЙ Harddisk N.
# Счётчик «сколько записей» без самих записей ничего не дискриминирует: нужен Status Code
# каждой ошибки — media/data integrity это или transport/timeout (то есть диск отваливался
# по шине, а не сыпался). Плюс \Device\RaidPortN из журнала надо чем-то привязать к диску.

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NvmeErrLog
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec,
        uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize,
        byte[] outBuf, int outSize, out int returned, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    const uint IOCTL = 0x2D1400;   // IOCTL_STORAGE_QUERY_PROPERTY
    const int PropertyId = 50;     // StorageDeviceProtocolSpecificProperty
    const int ProtoNvme = 3, DataTypeLogPage = 2;
    const int HeaderSize = 8, SpecificSize = 40;

    public static byte[] Read(int driveNumber, int logPage, int logSize)
    {
        IntPtr h = CreateFileW(@"\\.\PhysicalDrive" + driveNumber, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1))
            throw new Exception("CreateFile failed, win32=" + Marshal.GetLastWin32Error());
        try
        {
            int total = HeaderSize + SpecificSize + logSize;
            byte[] buf = new byte[total];
            BitConverter.GetBytes(PropertyId).CopyTo(buf, 0);
            BitConverter.GetBytes(0).CopyTo(buf, 4);
            BitConverter.GetBytes(ProtoNvme).CopyTo(buf, 8);
            BitConverter.GetBytes(DataTypeLogPage).CopyTo(buf, 12);
            BitConverter.GetBytes(logPage).CopyTo(buf, 16);
            BitConverter.GetBytes(0).CopyTo(buf, 20);
            BitConverter.GetBytes(SpecificSize).CopyTo(buf, 24);
            BitConverter.GetBytes(logSize).CopyTo(buf, 28);

            byte[] outBuf = new byte[total];
            int ret;
            if (!DeviceIoControl(h, IOCTL, buf, total, outBuf, total, out ret, IntPtr.Zero))
                throw new Exception("DeviceIoControl failed, win32=" + Marshal.GetLastWin32Error());

            byte[] log = new byte[logSize];
            Array.Copy(outBuf, HeaderSize + SpecificSize, log, 0, logSize);
            return log;
        }
        finally { CloseHandle(h); }
    }
}
'@ -ErrorAction Stop

# Status Code Type (SCT) + Status Code (SC) — то, ради чего всё и затевалось.
function Get-NvmeStatusText { param([int]$Sct, [int]$Sc)
    $sctName = switch ($Sct) {
        0 { 'Generic' } 1 { 'Command Specific' } 2 { 'Media/Data Integrity' }
        3 { 'Path Related' } 7 { 'Vendor Specific' } default { "SCT=$Sct" }
    }
    $scName = if ($Sct -eq 0) {
        switch ($Sc) {
            0x00 { 'Successful Completion' } 0x01 { 'Invalid Command Opcode' }
            0x02 { 'Invalid Field in Command' } 0x04 { 'Data Transfer Error' }
            0x05 { 'Aborted - Power Loss' } 0x06 { 'Internal Error' }
            0x07 { 'Command Abort Requested' } 0x08 { 'Aborted - SQ Deletion' }
            0x0B { 'Aborted - Failed Fused' } 0x1C { 'Command Aborted by Host' }
            default { ('SC=0x{0:X2}' -f $Sc) }
        }
    } elseif ($Sct -eq 2) {
        switch ($Sc) {
            0x80 { 'Write Fault' } 0x81 { 'Unrecovered Read Error' }
            0x82 { 'End-to-end Guard Check Error' } 0x86 { 'Compare Failure' }
            0x87 { 'Access Denied' } 0x88 { 'Deallocated or Unwritten Logical Block' }
            default { ('SC=0x{0:X2}' -f $Sc) }
        }
    } elseif ($Sct -eq 3) {
        switch ($Sc) {
            0x00 { 'Internal Path Error' } 0x60 { 'Controller Pathing Error' }
            0x70 { 'Host Pathing Error' } 0x71 { 'Command Aborted By Host' }
            default { ('SC=0x{0:X2}' -f $Sc) }
        }
    } else { ('SC=0x{0:X2}' -f $Sc) }
    "$sctName / $scName"
}

'=== Karta: disk -> SCSI-port (dlya privyazki \Device\RaidPortN iz zhurnala) ==='
Get-CimInstance Win32_DiskDrive -ErrorAction SilentlyContinue |
    Select-Object @{n='Device';e={$_.DeviceID}}, Model, SCSIPort, SCSIBus, SCSITargetId,
                  @{n='SN';e={$_.SerialNumber}} |
    Format-Table -Auto | Out-String
''

$disks = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object BusType -eq 'NVMe' | Sort-Object DeviceId
foreach ($d in $disks) {
    $num = [int]$d.DeviceId
    "=== PhysicalDrive$num : $($d.FriendlyName) — Error Information Log (page 01h) ==="
    try {
        # 64 записи * 64 байта; контроллер отдаёт столько, сколько поддерживает.
        $log = [NvmeErrLog]::Read($num, 1, 4096)
    } catch {
        "  OSHIBKA: $($_.Exception.Message)"; ''; continue
    }

    $shown = 0
    for ($i = 0; $i -lt 64; $i++) {
        $off = $i * 64
        $errCount = [BitConverter]::ToUInt64($log, $off)
        if ($errCount -eq 0) { continue }   # пустой слот
        $sqid   = [BitConverter]::ToUInt16($log, $off + 8)
        $cmdid  = [BitConverter]::ToUInt16($log, $off + 10)
        $status = [BitConverter]::ToUInt16($log, $off + 12)
        $sc     = ($status -shr 1) -band 0xFF
        $sct    = ($status -shr 9) -band 0x7
        $dnr    = ($status -shr 15) -band 1
        $lba    = [BitConverter]::ToUInt64($log, $off + 16)
        $ns     = [BitConverter]::ToUInt32($log, $off + 24)
        "  [{0,2}] errCount={1,-6} SQ={2,-4} CID={3,-6} status=0x{4:X4} -> {5}{6}  LBA={7} NS={8}" -f `
            $i, $errCount, $sqid, $cmdid, $status, (Get-NvmeStatusText $sct $sc), $(if ($dnr) { ' [DNR]' } else { '' }), $lba, $ns
        $shown++
    }
    if ($shown -eq 0) { '  (zapisey net)' }
    ''
}
