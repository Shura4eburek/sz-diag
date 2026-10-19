using SzDiag.Contracts;

namespace SzDiag.Hub.Tests;

public class TransferTrackerTests
{
    private readonly ManualTime _time = new();
    private TransferTracker New() => new(_time);

    [Fact]
    public void Start_Add_ReportsBytesAndSpeed()
    {
        var t = New();
        t.Start("r1", "161432", TransferDirection.Push, "occt", 1000);
        _time.Advance(TimeSpan.FromSeconds(2));
        t.Add("r1", 400);

        var item = Assert.Single(t.Snapshot());
        Assert.Equal(TransferState.Running, item.State);
        Assert.Equal(1000, item.TotalBytes);
        Assert.Equal(400, item.DoneBytes);
        Assert.Equal(200, item.BytesPerSecond, precision: 1);
    }

    [Fact]
    public void SetTotal_FillsUnknownTotal()
    {
        var t = New();
        t.Start("r1", "161432", TransferDirection.Push, "occt");
        t.SetTotal("r1", 5000);
        Assert.Equal(5000, t.Snapshot().Single().TotalBytes);
    }

    [Fact]
    public void Finish_Success_MarksDoneWithNote()
    {
        var t = New();
        t.Start("r1", "161432", TransferDirection.Push, "occt", 1000);
        t.Finish("r1", error: null, note: "скачано 0, пропущено 5");

        var item = t.Snapshot().Single();
        Assert.Equal(TransferState.Done, item.State);
        Assert.Equal("скачано 0, пропущено 5", item.Note);
        Assert.NotNull(item.FinishedAt);
    }

    [Fact]
    public void Finish_WithError_MarksFailed()
    {
        var t = New();
        t.Start("r1", "161432", TransferDirection.Pull, "C:\\dumps\\*.dmp");
        t.Finish("r1", error: "таймаут");

        var item = t.Snapshot().Single();
        Assert.Equal(TransferState.Failed, item.State);
        Assert.Equal("таймаут", item.Note);
    }

    [Fact]
    public void Finished_DroppedAfterKeepWindow_RunningKept()
    {
        var t = New();
        t.Start("done", "161432", TransferDirection.Push, "occt");
        t.Finish("done", null);
        t.Start("run", "161432", TransferDirection.Push, "tm5");

        _time.Advance(TransferTracker.KeepFinished + TimeSpan.FromSeconds(1));

        Assert.Equal("run", Assert.Single(t.Snapshot()).Id);
    }

    [Fact]
    public void UnknownId_IsIgnored()
    {
        var t = New();
        t.Add("нет", 10);
        t.SetTotal("нет", 10);
        t.Finish("нет", null);
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Snapshot_NewestFirst()
    {
        var t = New();
        t.Start("old", "1", TransferDirection.Push, "a");
        _time.Advance(TimeSpan.FromSeconds(1));
        t.Start("new", "1", TransferDirection.Push, "b");
        Assert.Equal(new[] { "new", "old" }, t.Snapshot().Select(x => x.Id));
    }
}
