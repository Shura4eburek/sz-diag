using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

public class WindowsUpdateFreezeTests
{
    [Fact]
    public void Freeze_KillsAllThreeServicesThroughRegistry()
    {
        // sc config на WaaSMedicSvc не проходит — берёт только реестр; а без медика
        // заморозка не переживает ребут: он воскрешает wuauserv (бэклог п.34b).
        var script = WindowsUpdateFreeze.BuildFreezeScript();

        foreach (var svc in new[] { "wuauserv", "UsoSvc", "WaaSMedicSvc" })
        {
            Assert.Contains($@"Services\{svc}", script);
            Assert.Contains($"Stop-Service {svc}", script);
        }
        Assert.Contains("-Name Start -Value 4", script);
    }

    // Бэклог п.150 (СЗ 161716): Stop-Service на wuauserv молча не срабатывает — служба
    // остаётся Running, а freeze рапортует «применилось не всё». Нужен принудительный kill
    // хост-процесса svchost этой службы, а не только Start=4 в реестре.
    [Fact]
    public void Freeze_ForceKillsHostProcess_WhenServiceStaysRunning()
    {
        var script = WindowsUpdateFreeze.BuildFreezeScript();

        foreach (var svc in WindowsUpdateFreeze.Services)
        {
            Assert.Contains($"Win32_Service -Filter \"Name='{svc}'\"", script);
        }
        Assert.Contains("Stop-Process -Id", script);
        Assert.Contains("-Force", script);
    }

    [Fact]
    public void Freeze_PointsClientAtNonexistentWsus()
    {
        // Самый надёжный глушитель: клиенту просто некуда идти, и это переживает
        // воскрешение служб «лекарем».
        var script = WindowsUpdateFreeze.BuildFreezeScript();

        Assert.Contains("WUServer -Value 'http://127.0.0.1:8530'", script);
        Assert.Contains("UseWUServer -Value 1", script);
        Assert.Contains("NoAutoUpdate -Value 1", script);
    }

    [Fact]
    public void Capture_ReadsServicesPoliciesAndPending()
    {
        var script = WindowsUpdateFreeze.BuildCaptureScript();

        Assert.Contains("'svc:wuauserv='", script);
        Assert.Contains("'pol:NoAutoUpdate='", script);
        Assert.Contains("pending.xml", script);
    }

    [Fact]
    public void ParseCapture_ReadsValuesAndEmpties()
    {
        var parsed = WindowsUpdateFreeze.ParseCapture(
            "svc:wuauserv=3\r\nsvc:UsoSvc=2\r\npol:NoAutoUpdate=\r\npending:False\r\n");

        Assert.Equal("3", parsed["svc:wuauserv"]);
        Assert.Equal("2", parsed["svc:UsoSvc"]);
        Assert.Equal("", parsed["pol:NoAutoUpdate"]);
    }

    // Полностью применённая заморозка: службы стоят (Start=4), политики на месте.
    private const string FrozenVerify =
        "svc:wuauserv=4\nstate:wuauserv=Stopped\nsvc:UsoSvc=4\nstate:UsoSvc=Stopped\n" +
        "svc:WaaSMedicSvc=4\nstate:WaaSMedicSvc=Stopped\n" +
        "pol:NoAutoUpdate=1\npol:WUServer=http://127.0.0.1:8530\nmarker:True\n";

    [Fact]
    public void CheckDetailed_TasksOnly_IsCosmeticNotBlocking()
    {
        // Регрессия (бэклог п.175): 11 задач оркестратора под TrustedInstaller не поддались —
        // а службы стоят и политика на месте, машина от WU-перезагрузки ЗАЩИЩЕНА. Это не
        // повод для exit 1.
        var stdout = FrozenVerify +
            @"task:\Microsoft\Windows\UpdateOrchestrator\Schedule Scan=Ready" + "\n";

        var check = WindowsUpdateFreeze.CheckAppliedDetailed(stdout);

        Assert.Empty(check.Blocking);
        Assert.Single(check.Cosmetic);
        Assert.True(check.IsProtected);
    }

    [Fact]
    public void CheckDetailed_RunningService_IsBlocking()
    {
        var stdout = FrozenVerify.Replace("svc:wuauserv=4", "svc:wuauserv=3")
            .Replace("state:wuauserv=Stopped", "state:wuauserv=Running");

        var check = WindowsUpdateFreeze.CheckAppliedDetailed(stdout);

        Assert.NotEmpty(check.Blocking);
        Assert.False(check.IsProtected);
    }

    [Fact]
    public void StatusVerdict_NoMarkersAnywhere_IsNormalStateNotFailure()
    {
        // Регрессия (бэклог п.115): после честного unfreeze `freeze --status` кричал
        // «заморозка НЕ полная» и советовал заморозить обратно. Нет маркеров — нет и
        // заморозки, состояние штатное.
        var check = WindowsUpdateFreeze.CheckAppliedDetailed("svc:wuauserv=3\nmarker:False\n");

        var (code, verdict) = FreezeCommand.StatusVerdict(check,
            hostStateExists: false, clientMarker: false);

        Assert.Equal(0, code);
        Assert.Contains("штатное", verdict);
    }

    [Fact]
    public void StatusVerdict_ActiveFreezeWithBlockingIssues_Fails()
    {
        var check = WindowsUpdateFreeze.CheckAppliedDetailed("svc:wuauserv=3\nstate:wuauserv=Running\n");

        var (code, _) = FreezeCommand.StatusVerdict(check, hostStateExists: true, clientMarker: true);

        Assert.Equal(1, code);
    }

    [Fact]
    public void StatusVerdict_FrozenElsewhere_StillEvaluated()
    {
        // Замораживали с другого хоста: файла у нас нет, но маркер на клиенте есть —
        // это активная заморозка, а не «штатное состояние».
        var check = WindowsUpdateFreeze.CheckAppliedDetailed("svc:wuauserv=3\nstate:wuauserv=Running\nmarker:True\n");

        var (code, _) = FreezeCommand.StatusVerdict(check, hostStateExists: false, clientMarker: true);

        Assert.Equal(1, code);
    }

    [Fact]
    public void StatusVerdict_HeldFreeze_IsSuccessEvenWithCosmetic()
    {
        var stdout = FrozenVerify +
            @"task:\Microsoft\Windows\UpdateOrchestrator\Schedule Scan=Ready" + "\n";
        var check = WindowsUpdateFreeze.CheckAppliedDetailed(stdout);

        var (code, verdict) = FreezeCommand.StatusVerdict(check, hostStateExists: true, clientMarker: true);

        Assert.Equal(0, code);
        Assert.Contains("держится", verdict);
    }

    [Fact]
    public void HasPendingTransaction_DetectsPendingXml()
    {
        Assert.True(WindowsUpdateFreeze.HasPendingTransaction("svc:wuauserv=3\npending:True"));
        Assert.False(WindowsUpdateFreeze.HasPendingTransaction("svc:wuauserv=3\npending:False"));
    }

    [Fact]
    public void Unfreeze_RestoresCapturedValues()
    {
        var previous = new Dictionary<string, string>
        {
            ["svc:wuauserv"] = "3",
            ["svc:UsoSvc"] = "2",
            ["svc:WaaSMedicSvc"] = "3",
            ["pol:NoAutoUpdate"] = "0",
        };

        var script = WindowsUpdateFreeze.BuildUnfreezeScript(previous);

        Assert.Contains(@"Services\wuauserv' -Name Start -Value 3", script);
        Assert.Contains(@"Services\UsoSvc' -Name Start -Value 2", script);
        Assert.Contains("NoAutoUpdate -Value 0", script);
        Assert.Contains("Start-Service wuauserv", script);
    }

    [Fact]
    public void Unfreeze_UnknownPreviousValues_RemovesPolicyAndSetsManual()
    {
        // Чего не было до нас — не должно остаться после: иначе машина уедет к клиенту
        // с нашей политикой и без обновлений безопасности.
        var script = WindowsUpdateFreeze.BuildUnfreezeScript(new Dictionary<string, string>());

        Assert.Contains("Remove-ItemProperty", script);
        Assert.Contains("WUServer", script);
        Assert.Contains(@"Services\wuauserv' -Name Start -Value 3", script);   // Manual — дефолт Windows
        Assert.DoesNotContain("-Name Start -Value 4", script);
    }

    [Fact]
    public void Unfreeze_EmptyStringValue_TreatedAsAbsent()
    {
        var script = WindowsUpdateFreeze.BuildUnfreezeScript(new Dictionary<string, string>
        {
            ["pol:NoAutoUpdate"] = "",
        });

        Assert.Contains("Remove-ItemProperty", script);
        Assert.DoesNotContain("NoAutoUpdate -Value ", script);
    }

    [Fact]
    public void TaskDisableScript_DisablesBothOrchestratorFolders()
    {
        // Бэклог п.39/113: под учёткой агента (админ) эти задачи не поддаются («Access is
        // denied» — владелец SYSTEM/TrustedInstaller). Отдельный скрипт нужен для повтора
        // ровно этих строк под SYSTEM через `exec --as-system`.
        var script = WindowsUpdateFreeze.BuildTaskDisableScript();

        foreach (var folder in WindowsUpdateFreeze.TaskFolders)
            Assert.Contains($"Get-ScheduledTask -TaskPath '{folder}'", script);
        Assert.Contains("Disable-ScheduledTask", script);
    }

    [Fact]
    public void TaskEnableScript_NoPreviousTaskState_EnablesAllOrchestratorTasks()
    {
        var script = WindowsUpdateFreeze.BuildTaskEnableScript(new Dictionary<string, string>());

        foreach (var folder in WindowsUpdateFreeze.TaskFolders)
            Assert.Contains($"Get-ScheduledTask -TaskPath '{folder}'", script);
        Assert.Contains("Enable-ScheduledTask", script);
    }

    [Fact]
    public void TaskEnableScript_WithPreviousReadyTask_EnablesOnlyThatTask()
    {
        var previous = new Dictionary<string, string>
        {
            [@"task:\Microsoft\Windows\UpdateOrchestrator\Schedule Scan"] = "Ready",
        };

        var script = WindowsUpdateFreeze.BuildTaskEnableScript(previous);

        Assert.Contains(@"Enable-ScheduledTask -TaskPath '\Microsoft\Windows\UpdateOrchestrator\' -TaskName 'Schedule Scan'", script);
    }
}

public class FreezeCommandStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"szfreeze-{Guid.NewGuid():N}");

    [Fact]
    public void IsFrozen_NoFile_False()
        => Assert.False(FreezeCommand.IsFrozen(_dir, "160636"));

    [Fact]
    public void IsFrozen_AfterStateWritten_True()
    {
        var path = FreezeCommand.StatePath(_dir, "160636");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");

        Assert.True(FreezeCommand.IsFrozen(_dir, "160636"));
        Assert.False(FreezeCommand.IsFrozen(_dir, "160705"));   // соседняя СЗ не задета
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
