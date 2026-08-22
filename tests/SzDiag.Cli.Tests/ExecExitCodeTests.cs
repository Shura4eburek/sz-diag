using SzDiag.Cli;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Проброс кода возврата из exec в exit code szcli: три разных исхода — успех,
/// exit 1 внутри скрипта и отказ агента — раньше все давали $LASTEXITCODE = 0, и поверх
/// exec нельзя было автоматизировать (бэклог п.103).</summary>
public class ExecExitCodeTests
{
    private static ExecResult Result(int exitCode, bool timedOut = false)
        => new("r", exitCode, "", "", TimedOut: timedOut);

    [Fact]
    public void Success_MapsToZero()
        => Assert.Equal(0, ExecExitCode.From(Result(0)));

    [Fact]
    public void ScriptExitCode_IsPassedThrough()
        => Assert.Equal(7, ExecExitCode.From(Result(7)));

    [Fact]
    public void AgentRefusal_MapsToThree()
        // Отказ агента («уже выполняет команду», ошибка запуска) приходит с кодом -1.
        => Assert.Equal(3, ExecExitCode.From(Result(-1)));

    [Fact]
    public void Timeout_MapsToFour()
        => Assert.Equal(4, ExecExitCode.From(Result(-1, timedOut: true)));

    [Fact]
    public void JobStatus_Running_IsZero()
    {
        var status = new ExecJobStatus("r", "j", Running: true, null, "", DateTimeOffset.Now, 0);
        Assert.Equal(0, ExecExitCode.FromStatus(status));
    }

    [Fact]
    public void JobStatus_FinishedWithNonZero_IsPassedThrough()
    {
        var status = new ExecJobStatus("r", "j", Running: false, 5, "", DateTimeOffset.Now, 0);
        Assert.Equal(5, ExecExitCode.FromStatus(status));
    }

    [Fact]
    public void JobStatus_AgentError_MapsToThree()
    {
        var status = new ExecJobStatus("r", "j", Running: false, null, "", DateTimeOffset.Now, 0,
            Error: "нет такой задачи");
        Assert.Equal(3, ExecExitCode.FromStatus(status));
    }
}
