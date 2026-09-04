using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>#94/бэклог п.155: рецепты в репозитории начинаются с плейсхолдера `$Sz = '000000'`,
/// который раньше правился прямо в рабочем дереве перед каждым прогоном. `--param Key=Value`
/// подставляет значение поверх плейсхолдера, не трогая файл на диске.</summary>
public class ExecParamsTests
{
    [Fact]
    public void ParseArgs_NoParams_ReturnsEmpty()
        => Assert.Empty(ExecParams.ParseArgs(new[] { "exec", "156864", "-f", "recipe.ps1" }));

    [Fact]
    public void ParseArgs_SingleParam_Parsed()
    {
        var parsed = ExecParams.ParseArgs(new[] { "exec", "156864", "-f", "r.ps1", "--param", "Sz=161716" });

        var pair = Assert.Single(parsed);
        Assert.Equal("Sz", pair.Key);
        Assert.Equal("161716", pair.Value);
    }

    [Fact]
    public void ParseArgs_MultipleParams_AllParsed()
    {
        var parsed = ExecParams.ParseArgs(new[]
            { "exec", "156864", "-f", "r.ps1", "--param", "Sz=161716", "--param", "MemG=8" });

        Assert.Equal(2, parsed.Count);
        Assert.Contains(parsed, p => p is ("Sz", "161716"));
        Assert.Contains(parsed, p => p is ("MemG", "8"));
    }

    [Fact]
    public void ParseArgs_ValueContainsEquals_KeepsWholeValue()
    {
        var parsed = ExecParams.ParseArgs(new[] { "--param", "Filter=Name='OCCT'" });

        var pair = Assert.Single(parsed);
        Assert.Equal("Filter", pair.Key);
        Assert.Equal("Name='OCCT'", pair.Value);
    }

    [Fact]
    public void ParseArgs_MissingEquals_Ignored()
        => Assert.Empty(ExecParams.ParseArgs(new[] { "--param", "JustAName" }));

    [Fact]
    public void Apply_NoParams_ReturnsScriptUnchanged()
    {
        var script = "$Sz = '000000'\nWrite-Output $Sz";
        Assert.Equal(script, ExecParams.Apply(script, Array.Empty<(string, string)>()));
    }

    [Fact]
    public void Apply_OverridesExistingDefaultAssignment()
    {
        var script = "$Sz = '000000'   # номер СЗ\nWrite-Output $Sz";

        var result = ExecParams.Apply(script, new[] { ("Sz", "161716") });

        // Плейсхолдер закомментирован, а не удалён — файл на диске не трогаем, это лишь копия.
        Assert.Contains("# --param перекрыл значение по умолчанию: $Sz = '000000'", result);
        Assert.Contains("$Sz = '161716'", result);
        // Наше присваивание должно идти РАНЬШЕ по тексту, чем закомментированная строка —
        // тогда порядок исполнения не важен: старой присвоение больше не выполняется вовсе.
        Assert.True(result.IndexOf("$Sz = '161716'") < result.IndexOf("# --param перекрыл"));
    }

    [Fact]
    public void Apply_NoExistingAssignment_JustPrepends()
    {
        var script = "Write-Output $MemG";

        var result = ExecParams.Apply(script, new[] { ("MemG", "8") });

        Assert.Contains("$MemG = '8'", result);
        Assert.Contains(script, result);
    }

    [Fact]
    public void Apply_DoesNotTouchUnrelatedVariableWithSamePrefix()
    {
        // $SzOther не должен пострадать от --param Sz=...
        var script = "$SzOther = 'x'\n$Sz = '000000'";

        var result = ExecParams.Apply(script, new[] { ("Sz", "161716") });

        Assert.Contains("$SzOther = 'x'", result);
    }

    [Fact]
    public void Apply_EscapesSingleQuoteInValue()
    {
        var result = ExecParams.Apply("Write-Output $Note", new[] { ("Note", "it's fine") });
        Assert.Contains("$Note = 'it''s fine'", result);
    }

    [Fact]
    public void WithAutoSz_NoExplicitSzParam_AddsIt()
    {
        var result = ExecParams.WithAutoSz(Array.Empty<(string, string)>(), "161716");

        var pair = Assert.Single(result);
        Assert.Equal(("Sz", "161716"), pair);
    }

    [Fact]
    public void WithAutoSz_ExplicitSzParam_ExplicitWins()
    {
        var result = ExecParams.WithAutoSz(new[] { ("Sz", "999999") }, "161716");

        var pair = Assert.Single(result);
        Assert.Equal("999999", pair.Value);
    }
}
