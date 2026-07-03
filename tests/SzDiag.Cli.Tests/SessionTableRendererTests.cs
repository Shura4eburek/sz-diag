using Spectre.Console;
using Spectre.Console.Rendering;
using SzDiag.Cli;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

public class SessionTableRendererTests
{
    private static string RenderToText(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200;
        console.Write(renderable);
        return writer.ToString();
    }

    [Fact]
    public void Render_IncludesSzIpAndStatusMarker()
    {
        var at = new DateTimeOffset(2026, 7, 1, 15, 30, 0, TimeSpan.Zero);
        var sessions = new List<SessionInfo>
        {
            new("156864", "10.0.0.42", "PC-1", SessionStatus.Online, at, at)
        };

        var text = RenderToText(SessionTableRenderer.Render(sessions));

        Assert.Contains("156864", text);
        Assert.Contains("10.0.0.42", text);
        Assert.Contains("online", text);
    }

    [Fact]
    public void Render_EmptyList_ShowsPlaceholder()
    {
        var text = RenderToText(SessionTableRenderer.Render(new List<SessionInfo>()));
        Assert.Contains("нет активных СЗ", text);
    }

    [Theory]
    [InlineData(45, "45сек")]
    [InlineData(60, "1мин 00сек")]
    [InlineData(344, "5мин 44сек")]
    [InlineData(3599, "59мин 59сек")]
    [InlineData(3600, "1ч 00мин")]
    [InlineData(3900, "1ч 05мин")]
    public void FormatElapsed_Formats(int seconds, string expected)
        => Assert.Equal(expected, SessionTableRenderer.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Render_RunningSession_ShowsActivityLabel()
    {
        var now = new DateTimeOffset(2026, 7, 1, 15, 30, 0, TimeSpan.Zero);
        var sessions = new List<SessionInfo>
        {
            new("156864", "10.0.0.42", "PC-1", SessionStatus.Online, now, now,
                "Тест OCCT", now.AddSeconds(-344))
        };

        var text = RenderToText(SessionTableRenderer.Render(sessions, now));

        Assert.Contains("Активность", text);
        Assert.Contains("Тест OCCT", text);
    }
}
