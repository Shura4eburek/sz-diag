namespace SzDiag.Cli;

/// <summary>`szcli disk snapshot <СЗ> --label "<текст>"` — карта скоростей по зонам + SMART +
/// журнал в один файл, с явной меткой «до destructive-операции».
///
/// Регрессия (бэклог п.213, СЗ 161972): дефект (просадка чтения 3800→9.6→0 МБ/с с ~464 ГБ)
/// поймали 21.08, а 25.08 по требованию клиента переустановили Windows с форматированием —
/// доказательная база осталась только в одном логе, повторить нельзя (формат+TRIM стёр те
/// самые ячейки). Перед любой операцией, меняющей содержимое накопителя, нужен снимок
/// «было»: карта скоростей + SMART + журнал одной командой, а не поиск логов по `pulled\`
/// после того, как диск уже переписан.</summary>
public static class DiskSnapshotScript
{
    /// <summary>Число зон сканирования по диску (равномерно от начала до конца) и размер
    /// одного чтения в зоне. 8×64 МБ достаточно, чтобы поймать явную деградацию (как на
    /// 161972 — просадка была видна уже на грубом скане), не растягивая снимок на минуты.</summary>
    public const int Zones = 8;
    public const int ChunkMb = 64;

    public static string Build() => $$"""
        '=== Диски: карта скоростей (последовательное чтение по {{Zones}} зонам, {{ChunkMb}} МБ каждая) ==='
        Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object {
            $disk = $_
            $path = '\\.\PhysicalDrive' + $disk.DeviceId
            "--- $($disk.FriendlyName) ($path, $([math]::Round($disk.Size/1GB)) GB) ---"
            try {
                $size = $disk.Size
                $zones = {{Zones}}
                $chunkBytes = {{ChunkMb}} * 1MB
                $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                try {
                    $buf = New-Object byte[] $chunkBytes
                    for ($z = 0; $z -lt $zones; $z++) {
                        $offset = [int64](($size - $chunkBytes) * $z / [math]::Max(1, $zones - 1))
                        $offset = $offset - ($offset % 4096)
                        try {
                            $fs.Seek($offset, [System.IO.SeekOrigin]::Begin) | Out-Null
                            $sw = [System.Diagnostics.Stopwatch]::StartNew()
                            $read = $fs.Read($buf, 0, $buf.Length)
                            $sw.Stop()
                            $mbps = if ($sw.Elapsed.TotalSeconds -gt 0) { [math]::Round(($read/1MB)/$sw.Elapsed.TotalSeconds, 1) } else { 0 }
                            "zone {0}/{1} offset={2}GB read={3}MB time={4}ms speed={5}MB/s" -f ($z+1), $zones, [math]::Round($offset/1GB, 1), [math]::Round($read/1MB), $sw.ElapsedMilliseconds, $mbps
                        } catch { "zone $($z+1)/$zones: ошибка чтения - $($_.Exception.Message)" }
                    }
                } finally { $fs.Dispose() }
            } catch { "не удалось открыть диск для скана скорости: $($_.Exception.Message) (нужны права администратора)" }
        }

        '=== SMART / надёжность ==='
        Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object {
            $c = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
            if ($c) {
                [PSCustomObject]@{
                    Disk                 = $_.FriendlyName
                    TempC                = $c.Temperature
                    WearPct              = $c.Wear
                    PowerOnHours         = $c.PowerOnHours
                    ReadErrorsTotal      = $c.ReadErrorsTotal
                    ReadErrorsUncorrect  = $c.ReadErrorsUncorrected
                    WriteErrorsTotal     = $c.WriteErrorsTotal
                    WriteErrorsUncorrect = $c.WriteErrorsUncorrected
                    ReallocatedSectors   = $c.ReallocatedSectorsCount
                } | Format-List | Out-String
            } else { "$($_.FriendlyName): SMART-счётчики недоступны (нет прав администратора?)" }
        }

        '=== Журнал: последние диско-связанные события ==='
        Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName=@('disk','Ntfs','stornvme','storahci')} -MaxEvents 30 -ErrorAction SilentlyContinue |
            Select-Object TimeCreated, Id, ProviderName, LevelDisplayName | Format-Table -Auto | Out-String
        """;
}
