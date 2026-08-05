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
    public void Run_CyrillicOutput_SurvivesRoundTrip()
    {
        // Регрессия (бэклог п.17/29/62): дочерний powershell писал stdout в кодовой странице
        // консоли (cp866 у headless-агента), и кириллица в diag.md/exec приезжала мусором
        // («�������� Windows 11 Pro»). Проверяем сквозной путь: скрипт → stdout → строка.
        var runner = new PowerShellRunner(utf8: true);

        var r = runner.Run("Write-Output 'Перевірка кирилиці: ёжик'", timeout: TimeSpan.FromSeconds(15));

        Assert.Contains("Перевірка кирилиці: ёжик", r.StdOut);
    }

    [Fact]
    public void Run_CyrillicInScriptBody_ParsesAndCompares()
    {
        // Скрипт уходит через -EncodedCommand (UTF-16LE), поэтому кириллица в теле
        // (литералы, сравнения) не должна ломать парсер PowerShell.
        var runner = new PowerShellRunner(utf8: true);

        var r = runner.Run("$s = 'ошибка'; if ($s -eq 'ошибка') { 'СОВПАЛО' }",
            timeout: TimeSpan.FromSeconds(15));

        Assert.Contains("СОВПАЛО", r.StdOut);
    }

    [Fact]
    public void Run_WithoutUtf8_StillWorksForAscii()
    {
        // PE-режим: шапку с [Console]::OutputEncoding не ставим (там она вешает процесс —
        // СЗ 159948), но обычный ASCII-вывод обязан работать как раньше.
        var runner = new PowerShellRunner(utf8: false);

        var r = runner.Run("Write-Output 'pe-ok'", timeout: TimeSpan.FromSeconds(15));

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("pe-ok", r.StdOut);
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
