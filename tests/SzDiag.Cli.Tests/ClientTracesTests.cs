using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Три следа, которые оставались на клиентских машинах: драйвер `R0lhmmon` (п.88),
/// 12 ГБ мусора от прогонов и задачи в Ready (п.56), задача без номера СЗ (п.99).</summary>
public class ClientTracesTests
{
    [Fact]
    public void Cleanup_RemovesDriverByRealServiceName()
    {
        // Имя сервиса начинается с R0 — по «очевидному» lhmmon уборка промахивалась.
        var script = ClientTraces.BuildCleanupScript();

        Assert.Contains("R0lhmmon", script);
        Assert.Contains("sc.exe stop", script);
        Assert.Contains("sc.exe delete", script);
    }

    [Fact]
    public void Cleanup_KeepsTasksOfTheLiveSession()
    {
        var script = ClientTraces.BuildCleanupScript(new[] { "szdiag-sshd-160306" });

        Assert.Contains("'szdiag-sshd-160306'", script);
        Assert.Contains("$keep -notcontains", script);
    }

    [Fact]
    public void Cleanup_TakesTasksByPrefix_SoNamelessOnesAreCaught()
    {
        var script = ClientTraces.BuildCleanupScript();

        Assert.Contains("szdiag*", script);   // szdiag-lhmmon без номера СЗ тоже попадает
        Assert.Contains("Unregister-ScheduledTask", script);
    }

    [Fact]
    public void Inventory_ReportsTasksDriversAndBigFiles()
    {
        var script = ClientTraces.BuildInventoryScript();

        Assert.Contains("'task:'", script);
        Assert.Contains("'service:'", script);
        Assert.Contains("'big:'", script);
        Assert.Contains("500MB", script);
    }

    [Fact]
    public void FindLeftovers_MarksTaskWithoutSzNumber()
    {
        var stdout = string.Join("\n", new[]
        {
            "task:szdiag-lhmmon=Ready",
            "task:szdiag-sshd-160306=Running",
            "service:R0lhmmon=Running/registered",
            "service:WinRing0_1_2_0=none",
            "dir:C:\\ProgramData\\szdiag\\jobs=12.4",
            "big:C:\\ProgramData\\szdiag\\iotest.bin=12.1",
        });

        var problems = ClientTraces.FindLeftovers(stdout);

        Assert.Contains(problems, p => p.Contains("szdiag-lhmmon") && p.Contains("без номера СЗ"));
        Assert.Contains(problems, p => p.Contains("szdiag-sshd-160306") && !p.Contains("без номера"));
        Assert.Contains(problems, p => p.Contains("R0lhmmon"));
        Assert.DoesNotContain(problems, p => p.Contains("WinRing0"));   // none — не проблема
        Assert.Contains(problems, p => p.Contains("iotest.bin") && p.Contains("12,1") || p.Contains("12.1"));
    }

    [Fact]
    public void FindLeftovers_CleanMachine_IsEmpty()
    {
        var stdout = "service:R0lhmmon=none\ndir:C:\\ProgramData\\szdiag\\jobs=none";

        Assert.Empty(ClientTraces.FindLeftovers(stdout));
    }

    [Fact]
    public void TaskName_FollowsSingleConvention()
        => Assert.Equal("szdiag-lhmmon-160636", ClientTraces.TaskName("lhmmon", "160636"));
}
