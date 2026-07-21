using System.Diagnostics;
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class PowerShellRunnerTests
{
    [Fact]
    public void Run_ExceedsTimeout_KillsProcessAndThrowsQuickly()
    {
        var runner = new PowerShellRunner();
        var sw = Stopwatch.StartNew();

        Assert.Throws<PowerShellTimeoutException>(() =>
            runner.Run("Start-Sleep -Seconds 5", timeout: TimeSpan.FromMilliseconds(500)));

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"должен убить процесс быстро, а не ждать все 5с (прошло {sw.Elapsed})");
    }

    [Fact]
    public void Run_WithinTimeout_ReturnsNormally()
    {
        var runner = new PowerShellRunner();

        var result = runner.Run("Write-Output 'ok'", timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ok", result.StdOut);
    }

    [Fact]
    public void Run_MultilinePipeline_ReturnsAllLines()
    {
        // Регрессия: скрипт раньше шёл через stdin `-Command -`, который в PowerShell 5.1
        // обрывает многострочные конвейеры (строка с хвостовым | или , рвётся) — до вывода
        // доходила лишь первая строка. Все секции RunDiag на живой машине выходили пустыми.
        var runner = new PowerShellRunner();
        var script = string.Join("\n", new[]
        {
            "'FIRST'",
            "1..3 |",
            "  ForEach-Object { \"LINE$_\" } |",
            "  Out-String",
        });

        var r = runner.Run(script, timeout: TimeSpan.FromSeconds(15));

        Assert.Contains("FIRST", r.StdOut);
        Assert.Contains("LINE1", r.StdOut);
        Assert.Contains("LINE2", r.StdOut);
        Assert.Contains("LINE3", r.StdOut);
    }
}
