using SzDiag.Agent;

namespace SzDiag.Agent.Tests;

/// <summary>szcli.cmd теряет `&` в аргументе без пробелов: PowerShell не квотирует его при
/// релонче через cmd.exe (`szcli hw resolve "PCI\VEN_10DE&DEV_..."` режется на две "команды" —
/// бэклог п.49). Обход через двойные кавычки внутри одинарных неочевиден. Рабочее решение —
/// `.ps1`-обёртка: вызов через `&` передаёт `$args` как готовый массив строк, минуя
/// реконструкцию и разбор командной строки в cmd.exe.</summary>
public class BuildDistScriptTests
{
    private static string RepoRoot() => TestPaths.RepoRoot();

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

    [Fact]
    public void SzcliPs1Wrapper_MissingExe_ExitsNonZero_NotSilentSuccess()
    {
        // R-M11 (ревью волны 1): если `& exe` бросает (файл не найден) ДО первого запуска
        // процесса, $LASTEXITCODE остаётся от чего-то другого (в т.ч. $null) — «exit
        // $LASTEXITCODE» тогда молча давал exit 0, маскируя отказ вопреки контракту кодов
        // szcli (0/N/2/3/4).
        var dir = Path.Combine(Path.GetTempPath(), $"szcli-ps1-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var wrapperPath = Path.Combine(dir, "szcli.ps1");
            File.WriteAllText(wrapperPath, WrapperBody());   // cli\SzDiag.Cli.exe заведомо не существует

            var runner = new PowerShellRunner();
            var r = runner.Run($"& '{wrapperPath}' list", throwOnError: false, timeout: TimeSpan.FromSeconds(20));

            Assert.NotEqual(0, r.ExitCode);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
