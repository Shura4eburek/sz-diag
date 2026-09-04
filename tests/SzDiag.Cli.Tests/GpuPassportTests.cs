using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>`szcli hw passport` — паспорт видеокарты для заявки в АСЦ одной командой
/// (бэклог п.146, СЗ 160705: SUBSYS и part number vBIOS не отдавала ни одна секция diag,
/// снимали отдельным рецептом уже под прогоном).</summary>
public class GpuPassportTests
{
    [Fact]
    public void ScriptFor_Gpu_ContainsSubsysAndVbiosPartNumberFields()
    {
        var script = GpuPassport.ScriptFor("gpu");

        Assert.NotNull(script);
        Assert.Contains("SUBSYS_", script);
        Assert.Contains("BiosString", script);
        Assert.Contains("Convert-HwString", script);
        Assert.Contains("DEVPKEY_PciDevice_CurrentLinkSpeed", script);
        Assert.Contains("DEVPKEY_PciDevice_CurrentLinkWidth", script);
        Assert.Contains("TDR", script);
    }

    [Fact]
    public void ScriptFor_UnknownScope_ReturnsNull()
    {
        Assert.Null(GpuPassport.ScriptFor("all"));
        Assert.Null(GpuPassport.ScriptFor("nope"));
    }
}
