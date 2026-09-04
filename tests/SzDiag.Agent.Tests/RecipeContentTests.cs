using SzDiag.Agent;

namespace SzDiag.Agent.Tests;

/// <summary>Синтаксическая и содержательная проверка отдельных рецептов
/// <c>tools/recipes/client/*.ps1</c>, которые нельзя тестировать через сборку C# (это просто
/// файлы, которые кладутся на клиента через <c>szcli exec -f</c>). Общий помощник ищет корень
/// репозитория так же, как <see cref="BuildDistScriptTests"/>.</summary>
public class RecipeContentTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SzDiag.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не нашёл корень репо (SzDiag.sln)");
    }

    private static string Recipe(string name)
        => File.ReadAllText(Path.Combine(RepoRoot(), "tools", "recipes", "client", name));

    /// <summary>Один PowerShell токенизирует все переданные файлы разом — синтаксическая ошибка
    /// в правке падает на сборке, а не на живой заявке (см. DiagnosticTests.AllProbeBodies_ParseAsValidPowerShell).</summary>
    private static void AssertAllParse(params string[] names)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"szrecipes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var n in names)
                File.WriteAllText(Path.Combine(dir, n), Recipe(n), new System.Text.UTF8Encoding(true));

            var check = $$"""
                $bad = @()
                foreach ($f in Get-ChildItem '{{dir}}' -Filter *.ps1) {
                    $errors = $null
                    [void][System.Management.Automation.PSParser]::Tokenize(
                        (Get-Content $f.FullName -Raw), [ref]$errors)
                    if ($errors.Count -gt 0) {
                        $bad += "$($f.Name): $($errors[0].Message) (строка $($errors[0].Token.StartLine))"
                    }
                }
                if ($bad.Count -gt 0) { $bad; exit 1 } else { 'all-ok' }
                """;
            var r = new PowerShellRunner().Run(check, throwOnError: false, timeout: TimeSpan.FromSeconds(60));
            Assert.True(r.ExitCode == 0 && r.StdOut.Contains("all-ok"),
                $"рецепты с ошибками разбора:\n{r.StdOut}\n{r.StdErr}");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void StartGameCs2_UnifiesProcessAndMapCheckIntoSingleVerdict()
    {
        // Регрессия (#133 / б.188, 160705): «cs2.exe: НЕ ПОДНЯЛСЯ» и «ботов в матче: 9»
        // печатались двумя независимыми строками — расхождение читалось по отдельности,
        // а не как одна ошибка. Теперь — единственный вердикт по прогону.
        var text = Recipe("start-game-cs2.ps1");

        Assert.Contains("ПРОГОН ЗАСЧИТАН", text);
        Assert.Contains("НЕ ПОДНЯЛСЯ", text);
        Assert.Contains("НЕ СОСТОЯЛСЯ", text);
        Assert.Contains("КАРТА НЕ ЗАГРУЗИЛАСЬ", text);
        // Старого разнобоя из двух независимых Write-Output про процесс и про ботов быть не должно.
        Assert.DoesNotContain("'cs2.exe: ' + $(if ($g)", text);
    }

    [Fact]
    public void StartGameCs2_ParsesAsValidPowerShell() => AssertAllParse("start-game-cs2.ps1");

    [Fact]
    public void PeOfflineTriage_HasWerLiveKernelSection()
    {
        // #136 / б.191 (161556): pe-offline-triage.ps1 (один заход по машине з PE) не дивився
        // Report.wer взагалі - справжня причина 35 x KP41 без BSOD/WHEA лежала саме там.
        var text = Recipe("pe-offline-triage.ps1");

        Assert.Contains("WER: LiveKernelEvent", text);
        Assert.Contains("ReportArchive", text);
        Assert.Contains("Kernel_|Critical_", text);
        Assert.Contains("VIDEO_ENGINE_TIMEOUT_DETECTED", text);
        Assert.Contains("вимкнон", text);   // звірка з Kernel-Power 41
    }

    [Fact]
    public void PeOfflineTriage_ParsesAsValidPowerShell() => AssertAllParse("pe-offline-triage.ps1");
}
