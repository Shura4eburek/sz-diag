using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

/// <summary>В diag.md ошибка секции затирала весь её вывод: на 161312 «История сбоев»
/// состояла ровно из строки `ошибка: код 1:` (бэклог п.74).</summary>
public class DiagReportBuilderTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 7, 3, 0, 0, TimeSpan.Zero);

    private static string Build(params TestStepResult[] steps)
        => DiagReportBuilder.Build(new TestReport("161312", "PC-1", At, steps));

    [Fact]
    public void FailedSection_PrintsBothErrorAndCollectedOutput()
    {
        var md = Build(new TestStepResult("История сбоев", TestStepKind.Command,
            Output: "CrashDumpEnabled=3", ExitCode: 1, Error: "код 1: stderr пуст"));

        Assert.Contains("ошибка: код 1: stderr пуст", md);
        Assert.Contains("CrashDumpEnabled=3", md);
    }

    [Fact]
    public void SuccessfulSection_PrintsOutputOnly()
    {
        var md = Build(new TestStepResult("Диски", TestStepKind.Command, Output: "OK", ExitCode: 0));

        Assert.Contains("## Диски", md);
        Assert.Contains("OK", md);
        Assert.DoesNotContain("ошибка", md);
    }
}
