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
