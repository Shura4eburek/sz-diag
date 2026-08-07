using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Попытка перезапустить агента скриптом стоила потери машины: `Stop-Process` убил и
/// агента, и сам скрипт, задача создаться не успела (бэклог п.83).</summary>
public class AgentRestartTests
{
    [Fact]
    public void Script_RegistersTaskBeforeTouchingTheAgent()
    {
        var script = AgentRestart.BuildScript("160306");

        var register = script.IndexOf("Register-ScheduledTask", StringComparison.Ordinal);
        var kill = script.IndexOf("Stop-Process", StringComparison.Ordinal);

        Assert.True(register > 0);
        // Убийство агента живёт ВНУТРИ задачи, поэтому в тексте оно идёт до регистрации —
        // важно, что сам скрипт никого не гасит: за это отвечает задача под SYSTEM.
        Assert.Contains("New-ScheduledTaskPrincipal -UserId 'SYSTEM'", script);
        Assert.True(kill < register, "Stop-Process должен быть частью аргумента задачи, а не отдельным шагом");
    }

    [Fact]
    public void Script_VerifiesTaskWasReallyCreated()
    {
        var script = AgentRestart.BuildScript("160306");

        Assert.Contains("Get-ScheduledTask -TaskName", script);
        Assert.Contains("ОШИБКА: задачу создать не удалось", script);
        Assert.Contains("агент НЕ трогаем", script);
    }

    [Fact]
    public void Script_PassesSzAsArgument_NotThroughConsole()
    {
        var script = AgentRestart.BuildScript("161432");

        Assert.Contains("-ArgumentList '161432'", script);
    }

    [Fact]
    public void Script_TakesExePathFromLiveProcess()
    {
        // Агент может лежать где угодно, в том числе внутри OneDrive клиента (п.63).
        var script = AgentRestart.BuildScript("160306");

        Assert.Contains("Get-Process -Name 'SzDiag.Agent'", script);
        Assert.Contains("$proc.Path", script);
    }

    [Fact]
    public void TaskName_SharesSzdiagPrefix_SoCleanupFindsIt()
    {
        Assert.StartsWith("szdiag-", AgentRestart.TaskName("160306"));
        Assert.Contains("160306", AgentRestart.TaskName("160306"));
        Assert.Contains(AgentRestart.TaskName("160306"), AgentRestart.BuildCleanupScript("160306"));
    }
}
