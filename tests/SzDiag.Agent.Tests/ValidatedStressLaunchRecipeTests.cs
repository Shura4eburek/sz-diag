namespace SzDiag.Agent.Tests;

/// <summary>`tools/recipes/client/validated-stress-launch.ps1` — обязательный шаг любого
/// самописного стресс-прогона (бэклог п.45, СЗ 160587): 12 процессов на 45 минут молча не
/// поднялись (PS 5.1 не грузит System.Numerics.Vectors без -ReferencedAssemblies), и это
/// выглядело "идущим" прогоном все 90 итераций мониторинга — 45 минут простоя вместо стресса.</summary>
public class ValidatedStressLaunchRecipeTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SzDiag.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не нашёл корень репо (SzDiag.sln)");
    }

    private static string RecipePath()
        => Path.Combine(RepoRoot(), "tools", "recipes", "client", "validated-stress-launch.ps1");

    [Fact]
    public void Recipe_Exists_WithUtf8Bom()
    {
        var path = RecipePath();
        Assert.True(File.Exists(path));

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "новые .ps1 обязаны быть UTF-8 с BOM (PowerShell 5.1 иначе ломает кириллицу)");
    }

    [Fact]
    public void Recipe_ValidatesLoadEarly_NotOnlyAtTheEnd()
    {
        var text = File.ReadAllText(RecipePath());

        Assert.Contains("ValidateAfterSeconds", text);
        Assert.Contains("HasExited", text);
        Assert.Contains("LoadPercentage", text);
        Assert.Contains("MinCpuLoadPercent", text);
        Assert.Contains("RedirectStandardError", text);
        Assert.Contains("throw", text);   // прерывает, а не досиживает до конца молча
    }

    [Fact]
    public void Recipe_WarnsAboutSystemNumericsUnderPs51()
    {
        // Сама грабля, которая породила рецепт, должна остаться в комментарии - иначе на
        // следующей заявке в неё наступят снова, не читая старый бэклог.
        var text = File.ReadAllText(RecipePath());

        Assert.Contains("ReferencedAssemblies", text);
        Assert.Contains("System.Numerics", text);
    }

    [Fact]
    public void Recipe_ParsesAsValidPowerShell()
    {
        var text = File.ReadAllText(RecipePath());

        var check = $$"""
            $errors = $null
            [void][System.Management.Automation.PSParser]::Tokenize(
                (Get-Content '{{RecipePath().Replace("\\", "\\\\")}}' -Raw), [ref]$errors)
            if ($errors.Count -gt 0) { $errors[0].Message; exit 1 } else { 'all-ok' }
            """;
        var r = new PowerShellRunner().Run(check, throwOnError: false, timeout: TimeSpan.FromSeconds(30));

        Assert.True(r.ExitCode == 0 && r.StdOut.Contains("all-ok"),
            $"ошибка разбора рецепта:\n{r.StdOut}\n{r.StdErr}");
    }
}
