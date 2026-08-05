using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class SessionRegistryTests
{
    private static SessionRegistry NewRegistry() => new();

    [Fact]
    public void Register_AddsOnlineSession()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        var active = reg.GetActive();
        var s = Assert.Single(active);
        Assert.Equal("156864", s.Sz);
        Assert.Equal("10.0.0.42", s.Ip);
        Assert.Equal("PC-1", s.Hostname);
        Assert.Equal(SessionStatus.Online, s.Status);
    }

    [Fact]
    public void Register_WithBootTime_StoresIt()
    {
        var reg = NewRegistry();
        var boot = new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.Zero);
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-1", boot);

        Assert.Equal(boot, Assert.Single(reg.GetActive()).BootTime);
    }

    [Fact]
    public void Register_SameBootTime_NotTreatedAsReboot()
    {
        // Переподключение агента (упал SignalR, сеть моргнула) — машина при этом не ребутилась.
        var reg = NewRegistry();
        var boot = new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.Zero);
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-1", boot);

        var rebooted = reg.Register("160306", "10.0.0.42", "PC-1", "conn-2", boot);

        Assert.False(rebooted.Rebooted);
        Assert.Null(Assert.Single(reg.GetActive()).LastRebootAt);
    }

    [Fact]
    public void Register_ChangedBootTime_DetectsReboot()
    {
        var reg = NewRegistry();
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-1",
            new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.Zero));

        var rebooted = reg.Register("160306", "10.0.0.42", "PC-1", "conn-2",
            new DateTimeOffset(2026, 7, 28, 13, 05, 0, TimeSpan.Zero));

        Assert.True(rebooted.Rebooted);
        Assert.NotNull(Assert.Single(reg.GetActive()).LastRebootAt);
    }

    [Fact]
    public void Register_BootTimeUnknown_NoFalseReboot()
    {
        // Агент старой сборки boot-time не шлёт: молчание не должно выглядеть как ребут.
        var reg = NewRegistry();
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-1",
            new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.Zero));

        var rebooted = reg.Register("160306", "10.0.0.42", "PC-1", "conn-2", bootTime: null);

        Assert.False(rebooted.Rebooted);
    }

    [Fact]
    public void Register_SameSzTwice_ReplacesConnection()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.Register("156864", "10.0.0.43", "PC-1", "conn-2");

        Assert.Single(reg.GetActive());
        Assert.Equal("conn-2", reg.TryGetConnectionId("156864"));
    }

    [Fact]
    public void Heartbeat_UpdatesLastHeartbeatAndSetsOnline()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.MarkOfflineByConnection("conn-1");

        var updated = reg.Heartbeat("156864");

        Assert.True(updated);
        Assert.Equal(SessionStatus.Online, reg.GetActive().Single().Status);
    }

    [Fact]
    public void Heartbeat_UnknownSz_ReturnsFalse()
    {
        var reg = NewRegistry();
        Assert.False(reg.Heartbeat("000000"));
    }

    [Fact]
    public void MarkOfflineByConnection_SetsStatusOffline()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        var sz = reg.MarkOfflineByConnection("conn-1");

        Assert.Equal("156864", sz);
        Assert.Equal(SessionStatus.Offline, reg.GetActive().Single().Status);
    }

    [Fact]
    public void Remove_DeletesSession()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        reg.Remove("156864");

        Assert.Empty(reg.GetActive());
        Assert.Null(reg.TryGetConnectionId("156864"));
    }

    [Fact]
    public void TryGetConnectionId_UnknownSz_ReturnsNull()
    {
        var reg = NewRegistry();
        Assert.Null(reg.TryGetConnectionId("000000"));
    }

    [Fact]
    public void SetActivity_UpdatesActivityAndSince()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var since = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

        var ok = reg.SetActivity("156864", "Тест OCCT", since);

        Assert.True(ok);
        var s = reg.GetActive().Single();
        Assert.Equal("Тест OCCT", s.Activity);
        Assert.Equal(since, s.ActivitySince);
    }

    [Fact]
    public void SetActivity_UnknownSz_ReturnsFalse()
        => Assert.False(NewRegistry().SetActivity("000000", "x", null));

    [Fact]
    public void Heartbeat_PreservesActivity()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.SetActivity("156864", "Тест OCCT", DateTimeOffset.UtcNow);

        reg.Heartbeat("156864");

        Assert.Equal("Тест OCCT", reg.GetActive().Single().Activity);
    }
}
