using SzDiag.Contracts;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public class HubPollerTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SessionInfo S(string sz) => new(sz, "10.0.0.5", "PC", SessionStatus.Online,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static TransferInfo T(TransferState state) => new("r1", "161432", TransferDirection.Push, "occt",
        100, 10, 5, DateTimeOffset.UtcNow, state);

    [Fact]
    public async Task Poll_Sessions_UpdatesSnapshotAndRaisesChanged()
    {
        var api = new FakeHubApi { Sessions = () => new[] { S("161432") } };
        var clock = new Clock();
        var p = new HubPoller(api, clock);
        HubSnapshot? raised = null;
        p.Changed += s => raised = s;

        await p.PollOnceAsync(PollKind.Sessions, default);

        Assert.Equal("161432", Assert.Single(p.Current.Sessions).Sz);
        Assert.Equal(clock.Now, p.Current.SessionsOkAt);
        Assert.False(p.Current.IsStale);
        Assert.Same(p.Current, raised);
    }

    [Fact]
    public async Task Poll_WhenHubDown_KeepsPreviousAndMarksStale()
    {
        var up = true;
        var api = new FakeHubApi { Sessions = () => up ? new[] { S("161432") } : throw new HttpRequestException("refused") };
        var p = new HubPoller(api, new Clock());
        await p.PollOnceAsync(PollKind.Sessions, default);

        up = false;
        await p.PollOnceAsync(PollKind.Sessions, default);

        Assert.Single(p.Current.Sessions);
        Assert.True(p.Current.IsStale);
        Assert.Equal(1, p.Current.Failures);
    }

    [Fact]
    public async Task Poll_FirstCallFails_EmptyButStale()
    {
        var api = new FakeHubApi { Sessions = () => throw new HttpRequestException("refused") };
        var p = new HubPoller(api, new Clock());
        await p.PollOnceAsync(PollKind.Sessions, default);

        Assert.Empty(p.Current.Sessions);
        Assert.True(p.Current.IsStale);
        Assert.Null(p.Current.SessionsOkAt);
    }

    [Fact]
    public async Task Poll_RecoveryClearsError()
    {
        var up = false;
        var api = new FakeHubApi { Sessions = () => up ? new[] { S("1") } : throw new HttpRequestException("x") };
        var p = new HubPoller(api, new Clock());
        await p.PollOnceAsync(PollKind.Sessions, default);
        up = true;
        await p.PollOnceAsync(PollKind.Sessions, default);

        Assert.False(p.Current.IsStale);
        Assert.Equal(0, p.Current.Failures);
    }

    [Fact]
    public void NextDelay_Base()
    {
        var p = new HubPoller(new FakeHubApi(), new Clock());
        Assert.Equal(TimeSpan.FromSeconds(2), p.NextDelay(PollKind.Sessions));
        Assert.Equal(TimeSpan.FromSeconds(5), p.NextDelay(PollKind.Transfers));
        Assert.Equal(TimeSpan.FromSeconds(10), p.NextDelay(PollKind.Health));
    }

    [Fact]
    public async Task NextDelay_Transfers_FastWhileRunning()
    {
        var api = new FakeHubApi { Transfers = () => new[] { T(TransferState.Running) } };
        var p = new HubPoller(api, new Clock());
        await p.PollOnceAsync(PollKind.Transfers, default);
        Assert.Equal(TimeSpan.FromSeconds(1), p.NextDelay(PollKind.Transfers));
    }

    [Fact]
    public async Task NextDelay_BacksOffOnFailures_CappedAt30s()
    {
        var api = new FakeHubApi { Sessions = () => throw new HttpRequestException("x") };
        var p = new HubPoller(api, new Clock());
        for (var i = 0; i < 10; i++) await p.PollOnceAsync(PollKind.Sessions, default);
        Assert.Equal(TimeSpan.FromSeconds(30), p.NextDelay(PollKind.Sessions));
    }

    [Fact]
    public async Task Poll_Health_AlsoFetchesStatus()
    {
        var status = new HubStatus("v1", new KbBackupStatus(false, null, null, null), new TunnelStatus(TunnelStates.Off, null));
        var api = new FakeHubApi
        {
            Health = () => new HealthzResponse(30, 1, 1, 1, 1, 0, DateTimeOffset.UtcNow),
            Status = () => status,
        };
        var p = new HubPoller(api, new Clock());
        await p.PollOnceAsync(PollKind.Health, default);
        Assert.Same(status, p.Current.Status);
    }
}
