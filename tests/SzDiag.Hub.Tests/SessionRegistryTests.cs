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
}
