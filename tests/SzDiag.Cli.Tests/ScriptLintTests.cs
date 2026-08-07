using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Сорок минут на 161312 ушло на «FurMark не запускается», а причина была в
/// приоритете операторов PowerShell: запятая связывает сильнее «+» (бэклог п.77).</summary>
public class ScriptLintTests
{
    [Fact]
    public void ConcatInsideArray_IsWarnedAbout()
    {
        var script = """
            $fm = 'C:\tools\furmark'
            $bl = @(
              '@echo off',
              'cd /d "' + $fm + '"',
              'furmark.exe --demo furmark-gl'
            )
            """;

        var warnings = ScriptLint.Check(script);

        Assert.NotEmpty(warnings);
        Assert.Contains("запятая связывает сильнее", warnings[0]);
    }

    [Fact]
    public void ParenthesisedConcat_IsFine()
    {
        var script = """
            $bl = @(
              '@echo off',
              ('cd /d "' + $fm + '"'),
              'furmark.exe'
            )
            """;

        Assert.Empty(ScriptLint.Check(script));
    }

    [Fact]
    public void PlainLiterals_AreFine()
    {
        var script = """
            $lines = @(
              '$smi = "C:\Windows\System32\nvidia-smi.exe"',
              '& $smi --query-gpu=utilization.gpu --format=csv'
            )
            """;

        Assert.Empty(ScriptLint.Check(script));
    }

    [Fact]
    public void SimpleConcatOutsideArray_IsFine()
    {
        // Обычная конкатенация без запятых ничего не ломает.
        Assert.Empty(ScriptLint.Check("$p = 'C:\\a' + $x + '\\b'\nWrite-Output $p"));
    }

    [Fact]
    public void EmptyScript_IsFine()
    {
        Assert.Empty(ScriptLint.Check(""));
        Assert.Empty(ScriptLint.Check(null!));
    }
}
