using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class BugcheckCodesTests
{
    [Theory]
    // Реальные коды с заявок: 190/340/26 приходили в diag.md десятичными и переводились руками.
    [InlineData(190u, "0xBE ATTEMPTED_WRITE_TO_READONLY_MEMORY")]
    [InlineData(340u, "0x154 UNEXPECTED_STORE_EXCEPTION")]
    [InlineData(26u, "0x1A MEMORY_MANAGEMENT")]
    [InlineData(292u, "0x124 WHEA_UNCORRECTABLE_ERROR")]
    [InlineData(80u, "0x50 PAGE_FAULT_IN_NONPAGED_AREA")]
    public void Format_KnownCode_ReturnsHexAndName(uint code, string expected)
        => Assert.Equal(expected, BugcheckCodes.Format(code));

    [Fact]
    public void Format_UnknownCode_ReturnsHexWithoutName()
    {
        // Незнакомый код всё равно печатаем в hex: это то, что ищется в поиске и в WinDbg.
        Assert.Equal("0xABCD", BugcheckCodes.Format(0xABCD));
    }

    [Fact]
    public void PowerShellPrologue_ContainsDecimalKeysAndHelper()
    {
        var ps = BugcheckCodes.PowerShellPrologue();

        // Ключ — decimal-строка: ровно в таком виде код лежит в поле события Kernel-Power 41.
        Assert.Contains("'190'='ATTEMPTED_WRITE_TO_READONLY_MEMORY'", ps);
        Assert.Contains("'340'='UNEXPECTED_STORE_EXCEPTION'", ps);
        Assert.Contains("function Fmt-Bug", ps);
        // Ноль — не «неизвестный код», а признак жёсткого обрыва питания.
        Assert.Contains("no BSOD - hard power loss", ps);
    }

    [Fact]
    public void PowerShellPrologue_IsAscii()
    {
        // Тела проб держим в ASCII (конвенция DiagnosticProbes): кириллица живёт в C#-именах секций.
        Assert.All(BugcheckCodes.PowerShellPrologue(), c => Assert.True(c < 128, $"не-ASCII символ: {c}"));
    }
}
