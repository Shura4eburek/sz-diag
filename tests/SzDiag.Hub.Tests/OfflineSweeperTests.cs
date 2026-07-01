using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class OfflineSweeperTests
{
    [Fact]
    public void MarkStaleOffline_MarksSessionsWithOldHeartbeat()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var reg = new SessionRegistry(time);
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        time.Advance(TimeSpan.FromSeconds(120));
        var affected = reg.MarkStaleOffline(TimeSpan.FromSeconds(60));

        Assert.Equal(new[] { "156864" }, affected);
        Assert.Equal(SessionStatus.Offline, reg.GetActive().Single().Status);
    }

    [Fact]
    public void MarkStaleOffline_FreshHeartbeat_NotMarked()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var reg = new SessionRegistry(time);
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        time.Advance(TimeSpan.FromSeconds(30));
        var affected = reg.MarkStaleOffline(TimeSpan.FromSeconds(60));

        Assert.Empty(affected);
        Assert.Equal(SessionStatus.Online, reg.GetActive().Single().Status);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
