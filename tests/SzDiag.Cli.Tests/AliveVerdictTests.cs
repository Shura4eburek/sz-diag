using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Регрессия (бэклог п.202, СЗ 161972): вердикт «вырубилась или висит» держался на
/// шести ручных прогонах — здесь он должен получаться одной функцией по собранным сигналам.</summary>
public class AliveVerdictTests
{
    [Fact]
    public void Describe_FreshHeartbeat_AliveForSure()
    {
        var msg = AliveVerdict.Describe(new AliveSignals(TimeSpan.FromSeconds(5), true, true, true));
        Assert.Contains("жива", msg);
    }

    [Fact]
    public void Describe_NoArpEntry_MeansPowerIsOff()
    {
        var msg = AliveVerdict.Describe(new AliveSignals(TimeSpan.FromMinutes(10), ArpFound: false, IcmpOk: false, AnyTcpOpen: false));
        Assert.Contains("питания нет", msg);
    }

    [Fact]
    public void Describe_ArpFoundAndTcpOpen_ProbablyBusyUnderLoad()
    {
        var msg = AliveVerdict.Describe(new AliveSignals(TimeSpan.FromMinutes(5), ArpFound: true, IcmpOk: false, AnyTcpOpen: true));
        Assert.Contains("задавлена нагрузкой", msg);
    }

    [Fact]
    public void Describe_ArpFoundIcmpOkNoTcp_Ambiguous()
    {
        var msg = AliveVerdict.Describe(new AliveSignals(TimeSpan.FromMinutes(5), ArpFound: true, IcmpOk: true, AnyTcpOpen: false));
        Assert.Contains("неоднозначное", msg);
    }

    [Fact]
    public void Describe_ArpFoundNothingElse_LooksHung()
    {
        var msg = AliveVerdict.Describe(new AliveSignals(TimeSpan.FromMinutes(5), ArpFound: true, IcmpOk: false, AnyTcpOpen: false));
        Assert.Contains("зависла", msg);
    }

    [Fact]
    public void Describe_NoHeartbeatAgeYet_FallsThroughToArpCheck()
    {
        // Сессия никогда не регистрировалась (СЗ неизвестна hub) — HeartbeatAge отсутствует.
        var msg = AliveVerdict.Describe(new AliveSignals(null, ArpFound: false, IcmpOk: false, AnyTcpOpen: false));
        Assert.Contains("питания нет", msg);
    }
}
