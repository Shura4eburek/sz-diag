using SzDiag.Contracts.Signing;
using SzDiag.Contracts.Signpost;
using SzDiag.Signpost;

namespace SzDiag.Signpost.Tests;

public class LeaseTableTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly SignedEnvelope Env = new("cA==", "cw==", "k");

    private static HubRecord Hub(string id, long seq, bool takeover = false)
        => new(id, id == "pc" ? "ПК" : "ноут", [new HubEndpoint(HubEndpointMode.Lan, $"http://{id}:5099")],
            null, seq, DateTimeOffset.UnixEpoch, takeover);

    [Fact]
    public void Empty_table_accepts_anyone()
    {
        var t = new LeaseTable(new Clock());
        var r = t.Publish(Hub("pc", 1), Env);
        Assert.Equal(LeaseOutcome.Accepted, r.Outcome);
        Assert.Equal("pc", t.Current!.HolderHubId);
    }

    [Fact]
    public void Same_hub_renews_with_growing_seq()
    {
        var clock = new Clock();
        var t = new LeaseTable(clock);
        t.Publish(Hub("pc", 1), Env);
        clock.Now += TimeSpan.FromSeconds(30);

        var r = t.Publish(Hub("pc", 2), Env);

        Assert.Equal(LeaseOutcome.Accepted, r.Outcome);
        Assert.Equal(clock.Now + LeaseTable.DefaultTtl, r.Lease!.ExpiresAt);
    }

    [Fact]
    public void Same_hub_with_old_seq_is_stale()
    {
        var t = new LeaseTable(new Clock());
        t.Publish(Hub("pc", 5), Env);
        Assert.Equal(LeaseOutcome.Stale, t.Publish(Hub("pc", 5), Env).Outcome);
        Assert.Equal(LeaseOutcome.Stale, t.Publish(Hub("pc", 4), Env).Outcome);
    }

    [Fact]
    public void Other_hub_without_takeover_conflicts_and_sees_holder()
    {
        var t = new LeaseTable(new Clock());
        t.Publish(Hub("pc", 1), Env);

        var r = t.Publish(Hub("laptop", 1), Env);

        Assert.Equal(LeaseOutcome.Conflict, r.Outcome);
        Assert.Equal("pc", r.Lease!.HolderHubId);
        Assert.Equal("ПК", r.Lease.HubName);
        Assert.Equal("pc", t.Current!.HolderHubId);
    }

    [Fact]
    public void Other_hub_with_takeover_wins_and_old_holder_then_conflicts()
    {
        var t = new LeaseTable(new Clock());
        t.Publish(Hub("pc", 1), Env);

        Assert.Equal(LeaseOutcome.Accepted, t.Publish(Hub("laptop", 1, takeover: true), Env).Outcome);
        Assert.Equal("laptop", t.Current!.HolderHubId);

        // Прежний держатель при продлении узнаёт, что потерял аренду.
        var lost = t.Publish(Hub("pc", 2), Env);
        Assert.Equal(LeaseOutcome.Conflict, lost.Outcome);
        Assert.Equal("laptop", lost.Lease!.HolderHubId);
    }

    [Fact]
    public void Expired_lease_is_taken_by_anyone_by_service_clock()
    {
        var clock = new Clock();
        var t = new LeaseTable(clock);
        t.Publish(Hub("pc", 1), Env);

        clock.Now += TimeSpan.FromSeconds(89);
        Assert.Equal(LeaseOutcome.Conflict, t.Publish(Hub("laptop", 1), Env).Outcome);

        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(LeaseOutcome.Accepted, t.Publish(Hub("laptop", 2), Env).Outcome);
        Assert.Equal("laptop", t.Current!.HolderHubId);
    }

    [Fact]
    public void Rejected_publish_does_not_burn_seq()
    {
        var t = new LeaseTable(new Clock());
        t.Publish(Hub("pc", 1), Env);
        Assert.Equal(LeaseOutcome.Conflict, t.Publish(Hub("laptop", 10), Env).Outcome);
        Assert.Equal(LeaseOutcome.Accepted, t.Publish(Hub("laptop", 10, takeover: true), Env).Outcome);
    }
}
