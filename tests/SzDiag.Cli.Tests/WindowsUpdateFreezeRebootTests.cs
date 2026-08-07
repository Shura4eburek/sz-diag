using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Заморозка не пережила ребут: `wuauserv` поднялся в `Start=3 Running`, а `freeze`
/// рапортовал успех по факту записи в реестр (бэклог п.72).</summary>
public class WindowsUpdateFreezeRebootTests
{
    private const string TaskKey = @"task:\Microsoft\Windows\UpdateOrchestrator\Schedule Scan";

    private static string Verify(string wuauStart, string wuauState, string taskState = "Disabled",
        string noAuto = "1", string wuServer = "http://127.0.0.1:8530")
        => string.Join("\n", new[]
        {
            $"svc:wuauserv={wuauStart}",
            $"state:wuauserv={wuauState}",
            "svc:UsoSvc=4", "state:UsoSvc=Stopped",
            "svc:WaaSMedicSvc=4", "state:WaaSMedicSvc=Stopped",
            $"pol:NoAutoUpdate={noAuto}",
            $"pol:WUServer={wuServer}",
            $"{TaskKey}={taskState}",
            "marker:True",
        });

    [Fact]
    public void CheckApplied_EverythingInPlace_NoProblems()
        => Assert.Empty(WindowsUpdateFreeze.CheckApplied(Verify("4", "Stopped")));

    [Fact]
    public void CheckApplied_ServiceRevivedAfterReboot_IsReported()
    {
        var problems = WindowsUpdateFreeze.CheckApplied(Verify("3", "Running"));

        Assert.Contains(problems, p => p.Contains("wuauserv") && p.Contains("Start=3"));
        Assert.Contains(problems, p => p.Contains("ЗАПУЩЕНА"));
    }

    [Fact]
    public void CheckApplied_OrchestratorTasksStillEnabled_IsReported()
    {
        var problems = WindowsUpdateFreeze.CheckApplied(Verify("4", "Stopped", taskState: "Ready"));

        Assert.Contains(problems, p => p.Contains("задачи оркестратора включены"));
    }

    [Fact]
    public void CheckApplied_PolicyMissing_IsReported()
    {
        var problems = WindowsUpdateFreeze.CheckApplied(Verify("4", "Stopped", noAuto: "", wuServer: ""));

        Assert.Contains(problems, p => p.Contains("NoAutoUpdate"));
        Assert.Contains(problems, p => p.Contains("WUServer"));
    }

    [Fact]
    public void FreezeScript_DisablesOrchestratorTasksAndLeavesMarker()
    {
        var script = WindowsUpdateFreeze.BuildFreezeScript();

        Assert.Contains("UpdateOrchestrator", script);
        Assert.Contains("Disable-ScheduledTask", script);
        Assert.Contains(WindowsUpdateFreeze.MarkerPath, script);
    }

    [Fact]
    public void UnfreezeScript_RestoresTasksThatWereReady_AndRemovesMarker()
    {
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [TaskKey] = "Ready",
            [@"task:\Microsoft\Windows\UpdateOrchestrator\Reboot"] = "Disabled",
        };

        var script = WindowsUpdateFreeze.BuildUnfreezeScript(previous);

        Assert.Contains("Enable-ScheduledTask -TaskPath", script);
        Assert.Contains("Schedule Scan", script);
        Assert.DoesNotContain("-TaskName 'Reboot'", script);   // была выключена до нас — не трогаем
        Assert.Contains("Remove-Item -Path '" + WindowsUpdateFreeze.MarkerPath, script);
    }

    [Fact]
    public void UnfreezeScript_WithoutSavedTasks_EnablesAllOfThem()
    {
        // Прежних значений нет (замораживали с другой машины): оставленная выключенной
        // задача WU опаснее лишнего включения.
        var script = WindowsUpdateFreeze.BuildUnfreezeScript(new Dictionary<string, string>());

        Assert.Contains("Enable-ScheduledTask -ErrorAction SilentlyContinue", script);
    }

    [Fact]
    public void CaptureScript_RemembersTaskStates()
    {
        var script = WindowsUpdateFreeze.BuildCaptureScript();

        Assert.Contains("UpdateOrchestrator", script);
        Assert.Contains("'task:'", script);
    }
}
