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

    [Fact]
    public void FailedSection_IsListedInHeaderSummary()
    {
        // Регрессия (бэклог п.149, СЗ 161716): секция whea упала целиком (длинный путь клиента),
        // а ошибка тонула внутри блока - отчёт выглядел полным. Упавшая секция обязана быть
        // видна сразу в шапке, а не только внутри своего "```"-блока.
        var md = Build(
            new TestStepResult("WHEA", TestStepKind.Command, Error: "имя файла слишком большую длину"),
            new TestStepResult("Диски", TestStepKind.Command, Output: "OK", ExitCode: 0));

        Assert.Contains("секция WHEA: НЕ ОТРАБОТАЛА", md);
        Assert.DoesNotContain("секция Диски: НЕ ОТРАБОТАЛА", md);
    }

    [Fact]
    public void AllSectionsOk_NoFailureSummaryPrinted()
    {
        var md = Build(new TestStepResult("Диски", TestStepKind.Command, Output: "OK", ExitCode: 0));

        Assert.DoesNotContain("НЕ ОТРАБОТАЛА", md);
    }

    // Бэклог п.139/150: заморозку WU не ставили неделями, и это было незаметно, пока не
    // закрывали СЗ. Незащищённая машина обязана кричать прямо в шапке diag.md.
    [Fact]
    public void WuNotFrozen_WarnsInHeader()
    {
        var md = DiagReportBuilder.Build(new TestReport("161716", "PC-1", At,
            new[] { new TestStepResult("Диски", TestStepKind.Command, Output: "OK", ExitCode: 0) },
            WuFrozen: false));

        Assert.Contains("Windows Update НЕ заморожен", md);
    }

    [Fact]
    public void WuFrozen_NoWarningPrinted()
    {
        var md = DiagReportBuilder.Build(new TestReport("161716", "PC-1", At,
            new[] { new TestStepResult("Диски", TestStepKind.Command, Output: "OK", ExitCode: 0) },
            WuFrozen: true));

        Assert.DoesNotContain("НЕ заморожен", md);
    }

    [Fact]
    public void WuFreezeUnknown_NoWarningPrinted()
    {
        // test run (не read-only diag) — проверка неприменима, шуметь незачем.
        var md = Build(new TestStepResult("Диски", TestStepKind.Command, Output: "OK", ExitCode: 0));

        Assert.DoesNotContain("заморожен", md);
    }
}
