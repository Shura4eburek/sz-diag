using SzDiag.Contracts;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public class HwProfileCacheTests
{
    private static readonly DateTimeOffset Boot = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualClock _clock = new();
    private readonly List<string> _calls = new();

    private static SessionInfo S(string sz, SessionStatus st = SessionStatus.Online, DateTimeOffset? boot = null)
        => new(sz, "10.0.0.5", "PC", st, Boot, Boot, BootTime: boot ?? Boot);

    private HwProfileCache New(Func<string, ExecResult?> exec)
        => new(new FakeHubApi { Exec = (sz, _) => { _calls.Add(sz); return exec(sz); } }, _clock);

    private static ExecResult Ok() => new("r", 0, HwProfileTests.Tuf9800, "");

    [Fact]
    public async Task OnlineFetchedOncePerBoot()
    {
        var c = New(_ => Ok());
        await c.Update(new[] { S("161432") });
        await c.Update(new[] { S("161432") });
        Assert.Equal(new[] { "161432" }, _calls);
        Assert.Equal("Ryzen 7 9800X3D", c.Get("161432")!.CpuShort);

        await c.Update(new[] { S("161432", boot: Boot.AddHours(1)) });   // свап железа = новый boot
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task OfflineNotFetched()
    {
        var c = New(_ => Ok());
        await c.Update(new[] { S("161432", SessionStatus.Offline) });
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task OneSzPerUpdate()
    {
        var c = New(_ => Ok());
        await c.Update(new[] { S("161432"), S("161501") });
        Assert.Single(_calls);
        await c.Update(new[] { S("161432"), S("161501") });
        Assert.Equal(new[] { "161432", "161501" }, _calls);
    }

    [Fact]
    public async Task Busy_NotRetriedFor5Minutes()
    {
        var c = New(_ => new ExecResult("r", -1, "", "агент уже выполняет команду"));
        await c.Update(new[] { S("161432") });
        await c.Update(new[] { S("161432") });
        Assert.Single(_calls);
        Assert.Null(c.Get("161432"));

        _clock.Advance(HwProfileCache.RetryAfter);
        await c.Update(new[] { S("161432") });
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task Timeout_TreatedAsBusy()
    {
        var c = New(_ => throw new TaskCanceledException());
        await c.Update(new[] { S("161432") });
        await c.Update(new[] { S("161432") });
        Assert.Single(_calls);
    }
}
