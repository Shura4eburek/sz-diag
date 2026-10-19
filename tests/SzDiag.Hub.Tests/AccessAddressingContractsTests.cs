using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

public class AccessAddressingContractsTests
{
    [Fact]
    public void Агент_старой_сборки_не_шлёт_новых_полей()
    {
        var request = new RegisterRequest("162003", "TEST-PC");

        Assert.Null(request.LanIp);
        Assert.Null(request.AccessHost);
        Assert.Null(request.AccessMode);
        Assert.Null(request.SshHostKeyFingerprint);
    }

    [Fact]
    public void SessionInfo_по_умолчанию_без_туннеля()
    {
        var now = DateTimeOffset.UtcNow;
        var info = new SessionInfo("162003", "192.168.1.5", "TEST-PC",
            SessionStatus.Online, now, now);

        Assert.Null(info.AccessHost);
        Assert.Null(info.AccessMode);
        Assert.Null(info.LanIp);
    }

    [Fact]
    public void Режимы_доступа_имеют_стабильные_имена()
    {
        // Значения ездят по SignalR и лежат в SQLite — менять нельзя.
        Assert.Equal("direct", AccessMode.Direct);
        Assert.Equal("tunnel", AccessMode.Tunnel);
    }

    [Fact]
    public void ReportAccess_переносит_имя_туннеля_и_отпечаток()
    {
        var report = new AccessReportRequest("162003", "abc-def.trycloudflare.com",
            AccessMode.Tunnel, "SHA256:abc123");

        Assert.Equal("162003", report.Sz);
        Assert.Equal("abc-def.trycloudflare.com", report.AccessHost);
        Assert.Equal(AccessMode.Tunnel, report.AccessMode);
        Assert.Equal("SHA256:abc123", report.SshHostKeyFingerprint);
    }
}
