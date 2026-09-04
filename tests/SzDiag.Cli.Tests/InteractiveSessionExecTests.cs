using System.Linq;
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

    // review W2 C-3: '*>' — синтаксис PowerShell, а исполняет строку cmd.exe, у которого он
    // не значит ничего осмысленного: '*' уезжает лишним токеном, а '>' перенаправляет только
    // stdout — stderr терялся бесследно.
    [Fact]
    public void BuildScript_UsesCmdRedirectionForBothStreams_NotPowerShellStar()
    {
        var wrapped = InteractiveSessionExec.BuildScript("156864", "'ok'");

        var innerLine = wrapped.Split('\n').Single(l => l.Contains("$inner ="));
        Assert.DoesNotContain("*>", innerLine);
        Assert.Contains("> \"", innerLine);
        Assert.Contains("2>&1", innerLine);
    }

    // review W2 C-3: свежезарегистрированная и запущенная задача в состоянии 'Ready' была
    // неотличима от «уже отработала» — первая же проверка сразу после Start-ScheduledTask могла
    // снять задачу до её реального старта. Буфер перед первым опросом даёт задаче время
    // перейти в 'Running'.
    [Fact]
    public void BuildScript_WaitsBeforeFirstPollAfterStart()
    {
        var wrapped = InteractiveSessionExec.BuildScript("156864", "'ok'");

        var startIdx = wrapped.IndexOf("Start-ScheduledTask", StringComparison.Ordinal);
        var sleepIdx = wrapped.IndexOf("Start-Sleep", StringComparison.Ordinal);
        var loopIdx = wrapped.IndexOf("while ((Get-Date)", StringComparison.Ordinal);
        Assert.True(startIdx >= 0 && sleepIdx > startIdx && sleepIdx < loopIdx,
            "между Start-ScheduledTask и циклом опроса должен быть Start-Sleep");
    }
}
