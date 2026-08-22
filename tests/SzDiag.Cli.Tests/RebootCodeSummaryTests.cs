using SzDiag.Cli;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>«BSOD ×13» без кодов не разделяет один почерк и три разных дефекта; код лежал
/// в том же событии Kernel-Power 41 и добывался ad-hoc рецептом (бэклог п.121).</summary>
public class RebootCodeSummaryTests
{
    private static RebootEvent Evt(string kind, long? bugcheck = null)
        => new("161346", DateTimeOffset.Now, null, null, null, null, kind,
            RebootSource.Journal, bugcheck);

    [Fact]
    public void FormatCode_Bsod_UsesHexAndName()
        => Assert.Equal("0xEF CRITICAL_PROCESS_DIED",
            RebootCodeSummary.FormatCode(Evt(ShutdownKind.Bsod, 239)));

    [Fact]
    public void FormatCode_HardOff_IsDash()
        => Assert.Equal("—", RebootCodeSummary.FormatCode(Evt(ShutdownKind.HardOff)));

    [Fact]
    public void Build_GroupsRepeatingCodes_MostFrequentFirst()
    {
        var events = new[]
        {
            Evt(ShutdownKind.Bsod, 239), Evt(ShutdownKind.Bsod, 239), Evt(ShutdownKind.Bsod, 239),
            Evt(ShutdownKind.Bsod, 292),                     // 0x124 WHEA_UNCORRECTABLE_ERROR
            Evt(ShutdownKind.HardOff), Evt(ShutdownKind.HardOff),
        };

        var lines = RebootCodeSummary.Build(events);

        Assert.Equal(2, lines.Count);
        Assert.Contains("0xEF CRITICAL_PROCESS_DIED ×3", lines[0]);
        Assert.Contains("0x124 WHEA_UNCORRECTABLE_ERROR ×1", lines[1]);
    }

    [Fact]
    public void Build_NoBsods_IsEmpty()
        => Assert.Empty(RebootCodeSummary.Build(new[] { Evt(ShutdownKind.HardOff) }));
}
