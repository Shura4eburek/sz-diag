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
    public void Cleanup_RemovesToolsDirectory_MovedOutOfCloud()
    {
        // Регрессия (бэклог п.63, СЗ 160705): ToolsDirectory уводит инструменты в
        // %ProgramData%\szdiag\tools, когда папка агента сама внутри OneDrive/Dropbox/… —
        // без этого доставленные ~250 МБ OCCT+lhmmon оставались на клиенте навсегда.
        var script = ClientTraces.BuildCleanupScript();

        Assert.Contains(@"C:\ProgramData\szdiag\tools", script);
    }

    [Fact]
    public void Inventory_ReportsToolsDirectorySize()
    {
        var script = ClientTraces.BuildInventoryScript();

        Assert.Contains(@"C:\ProgramData\szdiag\tools", script);
    }

    // Бэклог п.126/183 (СЗ 161346, 161312): «стоп OCCT» не снимал фоновый дисковый стресс и
    // lhmmon — единая команда должна бить всё разом.
    [Fact]
    public void StressStop_KillsKnownStressProcessesAndLhmmon()
    {
        var script = ClientTraces.BuildStressStopScript();

        foreach (var name in new[] { "OCCTCmd", "prime95", "y-cruncher", "lhmmon" })
            Assert.Contains($"'{name}'", script);
        Assert.Contains("Stop-Process -Force", script);
    }

    [Fact]
    public void StressStop_KillsDetachedExecJobProcesses()
    {
        var script = ClientTraces.BuildStressStopScript();

        Assert.Contains("szdiag\\\\jobs", script);
        Assert.Contains("Stop-Process -Id", script);
    }

    [Fact]
    public void StressStop_AlsoRemovesTasksAndDrivers_ButKeepsSessionTasks()
    {
        var script = ClientTraces.BuildStressStopScript(new[] { "szdiag-sshd-160306" });

        Assert.Contains("R0lhmmon", script);
        Assert.Contains("Unregister-ScheduledTask", script);
        Assert.Contains("'szdiag-sshd-160306'", script);
        Assert.Contains("$keep -notcontains", script);
    }

    [Fact]
    public void StressStop_DoesNotWipeTempDirs()
    {
        // Файлы — забота client cleanup/wipe-tools; stress stop только освобождает путь.
        var script = ClientTraces.BuildStressStopScript();

        Assert.DoesNotContain("Remove-Item", script);
    }

    [Fact]
    public void Cleanup_KillsIsolatedJobProcessTree_BeforeUnregisteringTask()
    {
        // Critical-4 (ревью волны 1): Unregister-ScheduledTask не убивает дерево процессов
        // изолированной задачи — без явного добивания OCCT/TM5 под SYSTEM оставался живым
        // после close, нарушая инвариант «доступ откатывается без следов».
        var script = ClientTraces.BuildCleanupScript();

        Assert.Contains("taskkill", script);
        Assert.Contains(@"C:\ProgramData\szdiag\jobs", script);
        Assert.True(script.IndexOf("taskkill", StringComparison.Ordinal)
            < script.IndexOf("Unregister-ScheduledTask", StringComparison.Ordinal),
            "процессы нужно добить ДО снятия задачи");
    }

    [Fact]
    public void TaskName_FollowsSingleConvention()
        => Assert.Equal("szdiag-lhmmon-160636", ClientTraces.TaskName("lhmmon", "160636"));

    [Fact]
    public void FindLeftoversDetailed_SeparatesLiveSessionTasksFromLeftovers()
    {
        // Регрессия (бэклог п.107): сразу после подъёма агента `client info` называл
        // рабочий доступ текущей сессии «остатками» и советовал cleanup — выполнить совет
        // значило снести себе sshd и watchdog посреди заявки.
        var stdout = string.Join("\n", new[]
        {
            "task:szdiag-sshd-160306=Running",
            "task:szdiag-watchdog-160306=Ready",
            "task:szdiag-autostart-160306=Ready",
            "task:szdiag-lhmmon=Ready",
        });

        var report = ClientTraces.FindLeftoversDetailed(stdout, "160306");

        Assert.Equal(3, report.CurrentSession.Count);
        var leftover = Assert.Single(report.Leftovers);
        Assert.Contains("szdiag-lhmmon", leftover);
    }

    [Fact]
    public void FindLeftoversDetailed_FreshSession_HasNoLeftovers()
    {
        var stdout = string.Join("\n", new[]
        {
            "task:szdiag-sshd-160306=Running",
            "task:szdiag-watchdog-160306=Ready",
            "task:szdiag-autostart-160306=Ready",
            "service:R0lhmmon=none",
        });

        var report = ClientTraces.FindLeftoversDetailed(stdout, "160306");

        Assert.Empty(report.Leftovers);
        Assert.Equal(3, report.CurrentSession.Count);
    }

    [Fact]
    public void AgentLogPath_ReadsLogLine_AndDoesNotLeakIntoLeftovers()
    {
        // Чек-лист отсылал к «agent.log рядом с exe» — лога там нет, он в logs\ (п.117).
        var stdout = "log:C:\\Agent\\logs\\agent.log\nservice:R0lhmmon=none";

        Assert.Equal(@"C:\Agent\logs\agent.log", ClientTraces.AgentLogPath(stdout));
        Assert.Empty(ClientTraces.FindLeftoversDetailed(stdout, "160306").Leftovers);
    }

    [Fact]
    public void AgentLogPath_NoLine_IsNull()
        => Assert.Null(ClientTraces.AgentLogPath("service:R0lhmmon=none"));

    [Fact]
    public void FindLeftoversDetailed_TasksOfOtherSz_AreLeftovers()
    {
        var report = ClientTraces.FindLeftoversDetailed("task:szdiag-sshd-159999=Ready", "160306");

        Assert.Empty(report.CurrentSession);
        Assert.Single(report.Leftovers);
    }

    [Fact]
    public void Inventory_ChecksPerfCountersHealth()
    {
        // Бэклог п.201 (СЗ 161972): Win32_PerfRawData_* давал "Invalid class" (0x80041010) на
        // клиенте со сломанными счётчиками — проверка должна попадать в инвентарь, а не
        // выясняться постфактум посреди прогона.
        var script = ClientTraces.BuildInventoryScript();

        Assert.Contains("Win32_PerfFormattedData_PerfProc_Process", script);
        Assert.Contains("'perf:ok'", script);
        Assert.Contains("'perf:broken='", script);
    }

    [Fact]
    public void PerfCountersBroken_ParsesErrorLine()
    {
        var stdout = "perf:broken=Invalid class\nservice:R0lhmmon=none";

        Assert.Equal("Invalid class", ClientTraces.PerfCountersBroken(stdout));
    }

    [Fact]
    public void PerfCountersBroken_Ok_IsNull()
        => Assert.Null(ClientTraces.PerfCountersBroken("perf:ok\nservice:R0lhmmon=none"));

    [Fact]
    public void PerfCountersBroken_NoLine_IsNull()
        => Assert.Null(ClientTraces.PerfCountersBroken("service:R0lhmmon=none"));
}
