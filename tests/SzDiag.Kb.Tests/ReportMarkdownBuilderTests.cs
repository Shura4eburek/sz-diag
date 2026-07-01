using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class ReportMarkdownBuilderTests
{
    private static TestReport Report(params TestStepResult[] steps) =>
        new("156864", "PC-1", new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), steps);

    [Fact]
    public void Build_IncludesHeaderWithSzHostAndDate()
    {
        var md = ReportMarkdownBuilder.Build(Report());
        Assert.Contains("# Отчёт диагностики — СЗ 156864", md);
        Assert.Contains("PC-1", md);
        Assert.Contains("2026-07-01", md);
    }

    [Fact]
    public void Build_CommandStep_ShowsCommandAndOutput()
    {
        var md = ReportMarkdownBuilder.Build(Report(
            new TestStepResult("Система", TestStepKind.Command, Command: "systeminfo", Output: "OS: Windows", ExitCode: 0)));
        Assert.Contains("## Система", md);
        Assert.Contains("systeminfo", md);
        Assert.Contains("OS: Windows", md);
    }

    [Fact]
    public void Build_FailedCommandStep_ShowsError()
    {
        var md = ReportMarkdownBuilder.Build(Report(
            new TestStepResult("Диски", TestStepKind.Command, Command: "smart", Error: "команда не найдена")));
        Assert.Contains("ошибка: команда не найдена", md);
    }

    [Fact]
    public void Build_ScreenshotStep_EmbedsImage()
    {
        var md = ReportMarkdownBuilder.Build(Report(
            new TestStepResult("Экран", TestStepKind.Screenshot, ScreenshotFile: "screen-1.png")));
        Assert.Contains("![[screen-1.png]]", md);
    }

    [Fact]
    public void Build_ScreenshotStep_Unavailable_ShowsError()
    {
        var md = ReportMarkdownBuilder.Build(Report(
            new TestStepResult("Экран", TestStepKind.Screenshot, Error: "нет сессии")));
        Assert.Contains("скрин недоступен: нет сессии", md);
    }
}
