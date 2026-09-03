using SzDiag.Agent;

namespace SzDiag.Agent.Tests;

/// <summary>szcli.cmd теряет `&` в аргументе без пробелов: PowerShell не квотирует его при
/// релонче через cmd.exe (`szcli hw resolve "PCI\VEN_10DE&DEV_..."` режется на две "команды" —
/// бэклог п.49). Обход через двойные кавычки внутри одинарных неочевиден. Рабочее решение —
/// `.ps1`-обёртка: вызов через `&` передаёт `$args` как готовый массив строк, минуя
/// реконструкцию и разбор командной строки в cmd.exe.</summary>
public class BuildDistScriptTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SzDiag.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не нашёл корень репо (SzDiag.sln)");
    }

    private static string WrapperBody()
    {
        var path = Path.Combine(RepoRoot(), "tools", "build-dist.ps1");
        var text = File.ReadAllText(path);
        var start = text.IndexOf("$szcliPs1 = @'", StringComparison.Ordinal);
        Assert.True(start >= 0, "не нашёл heredoc $szcliPs1 в build-dist.ps1");
        start = text.IndexOf('\n', start) + 1;
        var end = text.IndexOf("\n'@", start, StringComparison.Ordinal);
        Assert.True(end > start, "не нашёл конец heredoc $szcliPs1");
        return text[start..end];
    }

    [Fact]
    public void BuildDist_GeneratesSzcliPs1Wrapper()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "build-dist.ps1"));

        Assert.Contains("dist\\host\\szcli.ps1", text);
        Assert.Contains("@args", WrapperBody());
    }

    [Fact]
    public void SzcliPs1Wrapper_ForwardsAmpersandArgumentIntact()
    {
        // Регрессия: та же команда через .cmd теряет всё после '&' (см. класс). Через .ps1
        // аргумент обязан доехать байт-в-байт.
        var body = WrapperBody().Replace("SzDiag.Cli.exe", "cli-stub.ps1");
        var dir = Path.Combine(Path.GetTempPath(), $"szcli-ps1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "cli"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "cli", "cli-stub.ps1"), "Write-Output ($args -join '|')");
            var wrapperPath = Path.Combine(dir, "szcli.ps1");
            File.WriteAllText(wrapperPath, body);

            var runner = new PowerShellRunner();
            var r = runner.Run($"& '{wrapperPath}' hw resolve \"PCI\\VEN_10DE&DEV_2704\"",
                throwOnError: false, timeout: TimeSpan.FromSeconds(20));

            Assert.Contains(@"hw|resolve|PCI\VEN_10DE&DEV_2704", r.StdOut);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
