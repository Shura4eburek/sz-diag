using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>У WHEA-Logger Id=1 (fatal) именованных полей нет — всё в бинарной записи CPER,
/// и вся аналитика секции выходила пустой ровно там, где нужна (бэклог п.68).</summary>
public class CperDecoderTests
{
    [Fact]
    public void KnowsChannelsThatDiscriminateCpuFromPcie()
    {
        Assert.Equal("MCE (Machine Check Exception, CPU)",
            CperDecoder.NotificationTypes["e8f56ffe-919c-4cc5-ba88-65abe14913bb"]);
        Assert.Equal("PCIe error", CperDecoder.NotificationTypes["cf93c01f-1a16-4dfc-b8bc-9c4daf67c104"]);
        Assert.Equal("CMC (Corrected Machine Check, CPU)",
            CperDecoder.NotificationTypes["2dce8bb1-bdd7-450e-b9ad-9cf4ebd4f890"]);
    }

    [Fact]
    public void KnowsSectionTypesForCpuMemoryAndPcie()
    {
        Assert.Equal("Processor Specific (x86/x64)",
            CperDecoder.SectionTypes["dc3ea0b0-a144-4797-b95b-53fa242b6e1d"]);
        Assert.Equal("Platform Memory", CperDecoder.SectionTypes["a5bc1114-6f64-4ede-b863-3e83ed7c83b1"]);
        Assert.Equal("PCI Express", CperDecoder.SectionTypes["d995e954-bbc1-430f-ad91-b44dcb3c6f35"]);
    }

    [Fact]
    public void Prologue_IsAsciiAndCarriesTablesAndParser()
    {
        var ps = CperDecoder.PowerShellPrologue();

        Assert.All(ps, c => Assert.True(c < 128, $"не-ASCII в прологе CPER: {c}"));
        Assert.Contains("function Parse-Cper", ps);
        Assert.Contains("function Get-CperBytes", ps);
        Assert.Contains("'CPER'", ps);                                        // проверка сигнатуры
        Assert.Contains("e8f56ffe-919c-4cc5-ba88-65abe14913bb", ps);          // MCE
        Assert.Contains("[byte[]]$bytes[32..47]", ps);                        // срез приводится к byte[]
    }

    [Fact]
    public void Severity_FatalIsOne_AsInUefiSpec()
    {
        // Порядок в CPER контринтуитивен: 0 — Recoverable, 1 — Fatal, 2 — Corrected.
        Assert.Equal("Recoverable", CperDecoder.Severity[0]);
        Assert.Equal("Fatal", CperDecoder.Severity[1]);
        Assert.Equal("Corrected", CperDecoder.Severity[2]);
    }
}
