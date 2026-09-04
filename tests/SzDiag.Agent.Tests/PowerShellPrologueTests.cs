namespace SzDiag.Agent.Tests;

/// <summary>M-6 (ревью волны 1): прологи — сырые строковые литералы, заканчивающиеся на '}'
/// без \n. Конкатенация прологов друг с другом (или со следующей секцией, начинающейся с
/// оператора) молча ломается, если следующий текст стартует прямо со скобки/оператора без
/// разделителя. Хвостовой \n делает конкатенацию безопасной по построению, а не "повезло,
/// что PowerShell спарсил".</summary>
public class PowerShellPrologueTests
{
    [Fact]
    public void TimeZoneNote_PowerShellPrologue_EndsWithNewline()
        => Assert.EndsWith("\n", TimeZoneNote.PowerShellPrologue());

    [Fact]
    public void NvmeSmart_PowerShellPrologue_EndsWithNewline()
        => Assert.EndsWith("\n", NvmeSmart.PowerShellPrologue());
}
