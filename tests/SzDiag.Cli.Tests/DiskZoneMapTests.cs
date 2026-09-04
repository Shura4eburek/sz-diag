using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Карта скорости чтения по всему диску (`map`) + прицельный сплошной прогон по зоне
/// (`zone`), промотанные из рецепта `disk-zone-map.ps1` в CLI (бэклог п.200, СЗ 161972):
/// клиент назвал сценарий отказа («Victoria — диск 100%, скорость до 3 МБ/с»), а штатных
/// команд, которые его воспроизводят, не было вовсе.</summary>
public class DiskZoneMapTests
{
    [Fact]
    public void BuildScript_Map_InterpolatesParameters()
    {
        var script = DiskZoneMap.BuildScript(driveIndex: 1, mode: "map", points: 300, sampleMB: 16,
            zoneStartGB: 0, zoneEndGB: 0, zoneStepMB: 256, maxMinutes: 20, slowMBs: 150);

        Assert.Contains("$DriveIndex  = 1", script);
        Assert.Contains("$Mode        = 'map'", script);
        Assert.Contains("$Points      = 300", script);
        Assert.Contains("$SampleMB    = 16", script);
        Assert.Contains("$SlowMBs     = 150", script);
        Assert.Contains("$MaxMinutes  = 20", script);
    }

    [Fact]
    public void BuildScript_Zone_InterpolatesRange()
    {
        var script = DiskZoneMap.BuildScript(driveIndex: 0, mode: "zone", points: 0, sampleMB: 0,
            zoneStartGB: 440, zoneEndGB: 520, zoneStepMB: 256, maxMinutes: 25, slowMBs: 200);

        Assert.Contains("$Mode        = 'zone'", script);
        Assert.Contains("$ZoneStartGB = 440", script);
        Assert.Contains("$ZoneEndGB   = 520", script);
    }

    [Fact]
    public void BuildScript_UnknownMode_FallsBackToMap()
    {
        // Не должно упасть на мусорном значении — рабочий дефолт вместо ошибки на клиенте.
        var script = DiskZoneMap.BuildScript(1, "bogus", 100, 16, 0, 0, 256, 10, 200);

        Assert.Contains("$Mode        = 'map'", script);
    }

    [Fact]
    public void BuildScript_ReadsOnly_NeverOpensForWrite()
    {
        var script = DiskZoneMap.BuildScript(0, "map", 100, 16, 0, 0, 256, 10, 200);

        Assert.Contains("[IO.FileAccess]::Read", script);
        Assert.DoesNotContain("[IO.FileAccess]::Write", script);
    }

    [Fact]
    public void BuildScript_SnapshotsSmartBeforeAndAfter()
    {
        // Раньше карта, температура и SMART были тремя отдельными вызовами (бэклог п.200).
        var script = DiskZoneMap.BuildScript(0, "map", 100, 16, 0, 0, 256, 10, 200);

        Assert.Contains("Get-NvmeUnits", script);
        Assert.Contains("PercentageUsed", script);
        Assert.Contains("DataUnitsRead", script);
        Assert.Contains("prirost", script);   // прирост после прогона, а не абсолютное число
    }
}
