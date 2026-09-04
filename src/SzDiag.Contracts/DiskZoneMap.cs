namespace SzDiag.Contracts;

/// <summary>Карта скорости чтения по всему объёму накопителя точками (sampling) + прицельный
/// сплошной прогон по подозрительной зоне — то, что рисует график в Victoria/HD Tune, только
/// без GUI и одной командой CLI.
///
/// Промотано из рецепта <c>tools/recipes/client/disk-zone-map.ps1</c> (бэклог п.200, СЗ 161972):
/// клиент прямым текстом назвал сценарий отказа («Victoria — диск 100%, скорость до 3 МБ/с,
/// после этого компьютер вылетел»), а в арсенале не нашлось ничего, что его воспроизводит —
/// <c>szcli test run</c> гоняет OCCT/TM5/FurMark (накопитель не трогают вообще), а
/// <c>disk-stress.ps1</c> читает файлы случайным доступом (имитация загрузки игры). Сплошной
/// проход при сильной деградации не заканчивается (1863 ГБ на 10 МБ/с — больше суток), поэтому
/// два режима: <c>map</c> — sampling по всему диску за минуты, <c>zone</c> — сплошняк по узкому
/// диапазону, найденному картой.
///
/// SMART до/после (<c>PercentageUsed</c>, температура через <c>Get-NvmeSmartRows</c>-подобный
/// приём) снимается тем же заходом — раньше карта, температура и SMART были тремя отдельными
/// вызовами.</summary>
public static class DiskZoneMap
{
    /// <summary>Скрипт карты/зоны. ТОЛЬКО ЧТЕНИЕ: raw-устройство открывается на чтение,
    /// ни байта не пишется.</summary>
    public static string BuildScript(int driveIndex, string mode, int points, int sampleMB,
        double zoneStartGB, double zoneEndGB, int zoneStepMB, int maxMinutes, int slowMBs)
    {
        var modeEscaped = mode == "zone" ? "zone" : "map";
        return $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $DriveIndex  = {{driveIndex}}
            $Mode        = '{{modeEscaped}}'
            $Points      = {{points}}
            $SampleMB    = {{sampleMB}}
            $ZoneStartGB = {{zoneStartGB.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
            $ZoneEndGB   = {{zoneEndGB.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
            $ZoneStepMB  = {{zoneStepMB}}
            $MaxMinutes  = {{maxMinutes}}
            $SlowMBs     = {{slowMBs}}

            {{NvmeUnitsPrologue}}

            $disk = Get-CimInstance Win32_DiskDrive -Filter "Index=$DriveIndex"
            if (-not $disk) { "FATAL: PhysicalDrive$DriveIndex ne naiden"; exit 1 }
            $total = [int64]$disk.Size
            $isNvme = (Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object DeviceId -eq $DriveIndex).BusType -eq 'NVMe'
            $before = $null
            if ($isNvme) {
                $before = Get-NvmeUnits $DriveIndex
                if ($before) {
                    "SMART do starta: PercentageUsed={0}%, DataUnitsRead={1:N2} TB, DataUnitsWritten={2:N2} TB" -f `
                        $before.PercentageUsed, $before.DataUnitsReadTB, $before.DataUnitsWrittenTB
                }
            } else { "SMART: disk $DriveIndex ne NVMe (ili BusType ne opredelen) - SMART do/posle nedostupen." }

            $path = '\\.\PhysicalDrive' + $DriveIndex
            try {
                $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite, 1, [IO.FileOptions]::None)
            } catch { "FATAL: ne otkryt $path : $($_.Exception.Message)"; exit 1 }

            $deadline = (Get-Date).AddMinutes($MaxMinutes)
            $slow = @(); $results = @()

            if ($Mode -eq 'map') {
                "MAP: {0}, {1} GB, tochek {2} po {3} MB" -f $disk.Model.Trim(), [math]::Round($total/1GB,1), $Points, $SampleMB
                $buf = New-Object byte[] ($SampleMB * 1MB)
                $step = [int64]($total / $Points)
                $step = [int64]([math]::Floor($step / 1MB) * 1MB)
                for ($i = 0; $i -lt $Points; $i++) {
                    if ((Get-Date) -gt $deadline) { "STOP: limit $MaxMinutes min, sdelano $i tochek iz $Points"; break }
                    $off = [int64]$i * $step
                    if ($off + $buf.Length -gt $total) { break }
                    try { $fs.Position = $off } catch { "SEEK ERROR na $([math]::Round($off/1GB,1)) GB"; continue }
                    $t = [Diagnostics.Stopwatch]::StartNew()
                    try { $n = $fs.Read($buf, 0, $buf.Length) } catch {
                        "READ ERROR na $([math]::Round($off/1GB,1)) GB: $($_.Exception.Message)"; continue
                    }
                    $t.Stop()
                    $spd = if ($t.Elapsed.TotalSeconds -gt 0) { ($n/1MB) / $t.Elapsed.TotalSeconds } else { 0 }
                    $atGB = [math]::Round($off/1GB,1)
                    $results += [pscustomobject]@{ AtGB = $atGB; MBs = [math]::Round($spd,1) }
                    if ($spd -lt $SlowMBs) {
                        $slow += [pscustomobject]@{ AtGB = $atGB; MBs = [math]::Round($spd,1) }
                        "  PROSADKA: {0} GB  {1} MB/s" -f $atGB, [math]::Round($spd,1)
                    }
                    if ($i -gt 0 -and $i % 50 -eq 0) { "  ... $i / $Points tochek, prosadok $($slow.Count)" }
                }
            } else {
                "ZONE: sploshnoe chtenie $ZoneStartGB - $ZoneEndGB GB, shag otcheta $ZoneStepMB MB"
                $blk = 4MB
                $buf = New-Object byte[] $blk
                $pos = [int64]($ZoneStartGB * 1GB)
                $end = [int64]($ZoneEndGB * 1GB)
                if ($end -gt $total) { $end = $total }
                $fs.Position = $pos
                $winStart = $pos
                $winSw = [Diagnostics.Stopwatch]::StartNew()
                while ($pos -lt $end) {
                    if ((Get-Date) -gt $deadline) { "STOP: limit $MaxMinutes min na $([math]::Round($pos/1GB,2)) GB"; break }
                    try { $n = $fs.Read($buf, 0, $blk) } catch {
                        "READ ERROR na $([math]::Round($pos/1MB,0)) MB: $($_.Exception.Message)"
                        $pos += $blk; try { $fs.Position = $pos } catch { break }; continue
                    }
                    if ($n -le 0) { break }
                    $pos += $n
                    if (($pos - $winStart) -ge ($ZoneStepMB * 1MB)) {
                        $sec = $winSw.Elapsed.TotalSeconds
                        $mb = ($pos - $winStart)/1MB
                        $spd = if ($sec -gt 0) { $mb/$sec } else { 0 }
                        $atGB = [math]::Round($pos/1GB,2)
                        $results += [pscustomobject]@{ AtGB = $atGB; MBs = [math]::Round($spd,1) }
                        $line = "  {0,9} GB  {1,9} MB/s" -f $atGB, [math]::Round($spd,1)
                        if ($spd -lt $SlowMBs) { $line += "   <-- PROSADKA"; $slow += [pscustomobject]@{ AtGB = $atGB; MBs = [math]::Round($spd,1) } }
                        $line
                        $winStart = $pos; $winSw.Restart()
                    }
                }
            }
            $fs.Close()

            "----------------------------------------------------------"
            if ($results.Count -gt 0) {
                $st = $results.MBs | Measure-Object -Minimum -Maximum -Average
                "Tochek/okon: {0}   min {1} / avg {2} / max {3} MB/s" -f $results.Count, [math]::Round($st.Minimum,1), [math]::Round($st.Average,1), [math]::Round($st.Maximum,1)
            }
            "Prosadok nizhe $SlowMBs MB/s: $($slow.Count)"
            foreach ($s in ($slow | Select-Object -First 60)) { "   $($s.AtGB) GB : $($s.MBs) MB/s" }

            if ($before) {
                $after = Get-NvmeUnits $DriveIndex
                if ($after) {
                    "SMART posle: PercentageUsed={0}% (bylo {1}%), DataUnitsRead prirost={2:N2} TB, DataUnitsWritten prirost={3:N2} TB" -f `
                        $after.PercentageUsed, $before.PercentageUsed,
                        ($after.DataUnitsReadTB - $before.DataUnitsReadTB),
                        ($after.DataUnitsWrittenTB - $before.DataUnitsWrittenTB)
                } else { "SMART posle: log 02h ne prochitalsya povtorno." }
            }
            "DONE"
            """;
    }

    /// <summary>Пролог: <c>Get-NvmeUnits</c> — тот же приём, что и в
    /// <c>disk-stress-write.ps1</c> (NVMe Health Log page 02h напрямую через
    /// <c>IOCTL_STORAGE_QUERY_PROPERTY</c>), независимая копия для этого же скрипта.</summary>
    private const string NvmeUnitsPrologue = """
        function Get-NvmeUnits([int]$DriveNumber) {
            try {
                if (-not ([System.Management.Automation.PSTypeName]'SzDiagNvmeUnits').Type) {
                    Add-Type -ErrorAction Stop -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class SzDiagNvmeUnits {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr ov);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool CloseHandle(IntPtr h);
            const uint IOCTL = 0x2D1400;
            const int HeaderSize = 8, SpecificSize = 40, LogSize = 512;
            public static byte[] Read(int driveNumber) {
                IntPtr h = CreateFileW(@"\\.\PhysicalDrive" + driveNumber, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h == new IntPtr(-1)) throw new Exception("CreateFile PhysicalDrive" + driveNumber + " failed, win32=" + Marshal.GetLastWin32Error());
                try {
                    int total = HeaderSize + SpecificSize + LogSize;
                    byte[] buf = new byte[total];
                    BitConverter.GetBytes(50).CopyTo(buf, 0);
                    BitConverter.GetBytes(0).CopyTo(buf, 4);
                    BitConverter.GetBytes(3).CopyTo(buf, 8);
                    BitConverter.GetBytes(2).CopyTo(buf, 12);
                    BitConverter.GetBytes(2).CopyTo(buf, 16);
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
                } finally { CloseHandle(h); }
            }
        }
        '@
                }
                $log = [SzDiagNvmeUnits]::Read($DriveNumber)
                function ConvertTo-U128([byte[]]$l, [int]$o) {
                    $b = New-Object byte[] 17
                    [Array]::Copy($l, $o, $b, 0, 16)
                    [System.Numerics.BigInteger]::new($b)
                }
                [pscustomobject]@{
                    PercentageUsed     = [int]$log[5]
                    DataUnitsReadTB    = [double](ConvertTo-U128 $log 32) * 512000 / 1TB
                    DataUnitsWrittenTB = [double](ConvertTo-U128 $log 48) * 512000 / 1TB
                }
            } catch { $null }
        }
        """;
}
