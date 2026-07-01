using SzDiag.Cli;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

public class SessionTableRendererTests
{
    [Fact]
    public void Render_IncludesSzIpAndStatusMarker()
    {
        var at = new DateTimeOffset(2026, 7, 1, 15, 30, 0, TimeSpan.Zero);
        var sessions = new List<SessionInfo>
        {
            new("156864", "10.0.0.42", "PC-1", SessionStatus.Online, at, at)
        };

        var text = SessionTableRenderer.Render(sessions);

        Assert.Contains("156864", text);
        Assert.Contains("10.0.0.42", text);
        Assert.Contains("online", text);
    }

    [Fact]
    public void Render_EmptyList_ShowsPlaceholder()
    {
        var text = SessionTableRenderer.Render(new List<SessionInfo>());
        Assert.Contains("нет активных СЗ", text);
    }
}
