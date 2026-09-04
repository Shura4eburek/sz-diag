using System.Text;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>#194: `szcli exec <СЗ> --in-session "<script>"` прогоняет скрипт в интерактивной
/// сессии пользователя вместо session 0 агента (бэклог п.220, повтор на 111111 — "открой диск
/// в проводнике" через обычный exec стартовал explorer.exe в session 0, окна никто не видел).</summary>
public class InteractiveSessionExecTests
{
    [Fact]
    public void BuildScript_EmbedsInnerScriptAsBase64()
    {
        var inner = "Write-Output 'привет'";
        var wrapped = InteractiveSessionExec.BuildScript("156864", inner);

        var expected = Convert.ToBase64String(Encoding.Unicode.GetBytes(inner));
        Assert.Contains(expected, wrapped);
    }

    [Fact]
    public void BuildScript_UsesWin32ComputerSystemUserName_NotQuser()
    {
        var wrapped = InteractiveSessionExec.BuildScript("156864", "'ok'");

        Assert.Contains("Win32_ComputerSystem", wrapped);
        Assert.DoesNotContain("quser 2>", wrapped);
    }

    [Fact]
    public void BuildScript_UsesLimitedRunLevel()
    {
        // Highest молча не создаёт окно у GUI-запуска (см. InteractiveSessionRun) — тот же
        // принцип для произвольного скрипта: не элевейтить по умолчанию.
        var wrapped = InteractiveSessionExec.BuildScript("156864", "'ok'");

        Assert.Contains("-RunLevel Limited", wrapped);
    }

    [Fact]
    public void BuildScript_IncludesTaskNameWithSz()
    {
        var wrapped = InteractiveSessionExec.BuildScript("156864", "'ok'");

        Assert.Contains(InteractiveSessionExec.TaskName("156864"), wrapped);
    }

    [Fact]
    public void BuildScript_CleansUpTaskEvenOnFailure()
    {
        var wrapped = InteractiveSessionExec.BuildScript("156864", "'ok'");

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(wrapped, "Unregister-ScheduledTask").Count);
    }

    [Fact]
    public void BuildScript_ReadsOutputFileBack()
    {
        var wrapped = InteractiveSessionExec.BuildScript("156864", "'ok'");

        Assert.Contains("Get-Content", wrapped);
        Assert.Contains("-Raw", wrapped);
    }
}
