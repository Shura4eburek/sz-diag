$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# ПЕРИОДИЧЕСКИЙ снимок ключевых счётчиков NVMe в лог с флешем.
#
# Грабля (СЗ 161346, 18.08): счётчик `ErrorLogEntries` снимали ДО и ПОСЛЕ прогона — и это
# давало только «+6 за 3.5 часа», без привязки к моменту. А момент — это половина вывода:
# 17.08 ошибки легли в простое (14:07 и 14:39), тогда как нагрузка стартовала в 14:25, и
# понять это удалось лишь по журналу, а не по SMART.
# Вторая грабля: помещение обесточивают после 19:00, и если финальный снимок не успеть снять
# руками до рубильника — результат прогона теряется целиком. Здесь он пишется сам, на диск,
# с флешем: переживает и вырубон, и обрыв связи.
#
# Запускать detached: szcli exec <СЗ> -f tools\recipes\client\nvme-smart-watch.ps1 --detach

$Minutes  = 240   # 18.08 второй запуск: свет сегодня не рубят, добираем простой сверх вчерашних 3.5 ч
$EverySec = 300
$Log      = 'C:\ProgramData\szdiag\nvme-smart-watch.log'

New-Item -ItemType Directory -Path (Split-Path $Log) -Force -ErrorAction SilentlyContinue | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Log = $Log -replace '\.log$', "-$stamp.log"
$sw = [IO.StreamWriter]::new($Log, $false, [Text.UTF8Encoding]::new())
$sw.AutoFlush = $true
function Say([string]$m) { $line = "{0:HH:mm:ss}  {1}" -f (Get-Date), $m; $line; $sw.WriteLine($line) }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NvmeLogW
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec,
        uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize,
        byte[] outBuf, int outSize, out int returned, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    const uint IOCTL = 0x2D1400;
    const int PropertyId = 50, ProtoNvme = 3, DataTypeLogPage = 2, SmartLogPage = 2;
    const int HeaderSize = 8, SpecificSize = 40, LogSize = 512;
    public static byte[] Read(int driveNumber)
    {
        IntPtr h = CreateFileW(@"\\.\PhysicalDrive" + driveNumber, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) throw new Exception("CreateFile failed win32=" + Marshal.GetLastWin32Error());
        try {
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
            byte[] outBuf = new byte[total]; int ret;
            if (!DeviceIoControl(h, IOCTL, buf, total, outBuf, total, out ret, IntPtr.Zero))
                throw new Exception("DeviceIoControl failed win32=" + Marshal.GetLastWin32Error());
            byte[] log = new byte[LogSize];
            Array.Copy(outBuf, HeaderSize + SpecificSize, log, 0, LogSize);
            return log;
        } finally { CloseHandle(h); }
    }
}
'@ -ErrorAction Stop

function Get-U128 { param([byte[]]$Log, [int]$Offset)
    $b = New-Object byte[] 17
    [Array]::Copy($Log, $Offset, $b, 0, 16)
    [System.Numerics.BigInteger]::new($b)
}

$disks = @(Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object BusType -eq 'NVMe' | Sort-Object DeviceId)
Say "СТАРТ вахты SMART на $Minutes мин, снимок раз в $EverySec с. Дисков: $($disks.Count)"

# Опорные значения: дальше пишем ТОЛЬКО дельты и полную строку, чтобы прирост был виден глазами
$base = @{}
foreach ($d in $disks) {
    try {
        $l = [NvmeLogW]::Read([int]$d.DeviceId)
        $base["$($d.DeviceId)"] = @{ Err = (Get-U128 $l 176); Unsafe = (Get-U128 $l 144); Media = (Get-U128 $l 160) }
        Say ("  опора PhysicalDrive{0} {1}: ErrorLog={2} Unsafe={3} Media={4}" -f `
            $d.DeviceId, $d.FriendlyName, $base["$($d.DeviceId)"].Err, $base["$($d.DeviceId)"].Unsafe, $base["$($d.DeviceId)"].Media)
    } catch { Say "  опора PhysicalDrive$($d.DeviceId): ОШИБКА $($_.Exception.Message)" }
}

$deadline = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $EverySec
    foreach ($d in $disks) {
        $key = "$($d.DeviceId)"
        try {
            $l = [NvmeLogW]::Read([int]$d.DeviceId)
            $err = Get-U128 $l 176; $uns = Get-U128 $l 144; $med = Get-U128 $l 160
            $tempK = [BitConverter]::ToUInt16($l, 1)
            $b = $base[$key]
            $dErr = if ($b) { $err - $b.Err } else { 0 }
            $dUns = if ($b) { $uns - $b.Unsafe } else { 0 }
            $dMed = if ($b) { $med - $b.Media } else { 0 }
            $mark = if ($dErr -gt 0 -or $dMed -gt 0) { '  <<< ПРИРОСТ' } else { '' }
            Say ("  drive{0} {1}: ErrorLog={2} (+{3})  Unsafe={4} (+{5})  Media={6} (+{7})  {8}C{9}" -f `
                $d.DeviceId, $d.FriendlyName, $err, $dErr, $uns, $dUns, $med, $dMed, ($tempK - 273), $mark)
        } catch { Say "  drive${key}: ОШИБКА чтения SMART: $($_.Exception.Message)" }   # ${} обязательны: $key: PS читает как scope-префикс
    }
}
Say 'ФИНИШ вахты SMART.'
$sw.Dispose()
