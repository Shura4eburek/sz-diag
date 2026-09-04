using SzDiag.Cli;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>review W2 T-7 (критерий #194 — строка `client info` не была покрыта тестом): под
/// кем и в какой сессии живёт агент — на СЗ 123123 полчаса ушло на версии «UAC/Secure Desktop»,
/// пока `whoami` в exec не показал СИСТЕМА, хотя эта строка уже несла ответ.</summary>
public class ClientCommandTests
{
    private static SessionInfo Session(string? agentUser, int? agentSessionId) => new(
        "156864", "10.0.0.42", "PC-1", SessionStatus.Online,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        AgentUser: agentUser, AgentSessionId: agentSessionId);

    [Fact]
    public void FormatAgentIdentityLine_NoSession_ReturnsNull()
        => Assert.Null(ClientCommand.FormatAgentIdentityLine(null));

    [Fact]
    public void FormatAgentIdentityLine_OldAgentWithoutIdentity_ReturnsNull()
        => Assert.Null(ClientCommand.FormatAgentIdentityLine(Session(agentUser: null, agentSessionId: null)));

    [Fact]
    public void FormatAgentIdentityLine_InteractiveSession_ShowsUserAndSessionId()
    {
        var line = ClientCommand.FormatAgentIdentityLine(Session("DESKTOP-1\\kiril", 1));

        Assert.Contains(@"DESKTOP-1\kiril", line);
        Assert.Contains("session 1", line);
        Assert.DoesNotContain("GUI недоступен", line);
    }

    [Fact]
    public void FormatAgentIdentityLine_SessionZero_WarnsGuiUnavailable()
    {
        // Регрессия (бэклог п.220, СЗ 123123): автостарт-задача под SYSTEM живёт в session 0 —
        // Start-Process/скриншот там ломаются молча без этого явного предупреждения.
        var line = ClientCommand.FormatAgentIdentityLine(Session(@"NT AUTHORITY\СИСТЕМА", 0));

        Assert.Contains(@"NT AUTHORITY\СИСТЕМА", line);
        Assert.Contains("session 0", line);
        Assert.Contains("GUI недоступен", line);
    }

    [Fact]
    public void FormatAgentIdentityLine_UnknownSessionId_ShowsQuestionMark()
        => Assert.Contains("session ?", ClientCommand.FormatAgentIdentityLine(Session("user", null)));
}
