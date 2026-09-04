using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>`szcli disk snapshot` — карта скоростей + SMART + журнал в один файл, с меткой
/// «до/после» destructive-операции (бэклог п.213, СЗ 161972: переустановка с форматированием
/// стёрла единственное доказательство дефекта — снимок «до» никто не снял).</summary>
public class DiskSnapshotScriptTests
{
    [Fact]
    public void Build_ScansZonesByRawDiskHandle()
    {
        var script = DiskSnapshotScript.Build();

        Assert.Contains("PhysicalDrive", script);
        Assert.Contains("Get-PhysicalDisk", script);
        Assert.Contains("Stopwatch", script);
        Assert.Contains("MB/s", script);
    }

    [Fact]
    public void Build_IncludesSmartAndJournal()
    {
        var script = DiskSnapshotScript.Build();

        Assert.Contains("Get-StorageReliabilityCounter", script);
        Assert.Contains("ReadErrorsUncorrect", script);
        Assert.Contains("Get-WinEvent", script);
    }
}

public class DiskSnapshotNamingTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 25, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void BuildFileName_SanitizesLabelAndStampsTime()
    {
        var name = DiskSnapshotNaming.BuildFileName(At, "до переустановки!");

        Assert.StartsWith("20260825-143000-", name);
        Assert.EndsWith(".txt", name);
        Assert.DoesNotContain("!", name);
        Assert.DoesNotContain(" ", name);
    }

    [Fact]
    public void BuildFileName_EmptyLabel_FallsBackToSnapshot()
    {
        var name = DiskSnapshotNaming.BuildFileName(At, "!!!");
        Assert.Equal("20260825-143000-snapshot.txt", name);
    }

    [Fact]
    public void BuildFileName_TwoDifferentLabels_ProduceDifferentNames()
    {
        var before = DiskSnapshotNaming.BuildFileName(At, "до формата");
        var after = DiskSnapshotNaming.BuildFileName(At.AddHours(2), "после формата");

        Assert.NotEqual(before, after);
    }
}
