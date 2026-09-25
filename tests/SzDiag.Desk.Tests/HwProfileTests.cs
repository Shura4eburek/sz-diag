using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public class HwProfileTests
{
    // Ровно то, что печатает HwProfile.Script (161432: 9800X3D + TUF B650M-PLUS + 2×32 DDR5-6000).
    internal const string Tuf9800 =
        "cpu=AMD Ryzen 7 9800X3D 8-Core Processor\n" +
        "board=ASUSTeK COMPUTER INC.|TUF GAMING B650M-PLUS\n" +
        "mem=2|64|6000|KF560C36-32";

    [Fact]
    public void Parse_Line()
    {
        var p = HwProfile.Parse(Tuf9800)!;
        Assert.Equal("Ryzen 7 9800X3D · ASUS TUF GAMING B650M-PLUS · 2×32 ГБ @ 6000", p.Line);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i5-12400F", "i5-12400F")]
    [InlineData("AMD Ryzen 5 7500F 6-Core Processor", "Ryzen 5 7500F")]
    [InlineData("AMD Ryzen 7 5700G with Radeon Graphics", "Ryzen 7 5700G")]
    public void CpuShort(string name, string expected)
        => Assert.Equal(expected, new HwProfile(name, "", "", 0, 0, 0, "").CpuShort);

    [Fact]
    public void Parse_Garbage_Null() => Assert.Null(HwProfile.Parse("агент уже выполняет команду"));

    [Fact]
    public void Similarity_IdenticalBuilds_AllThree()
    {
        var a = HwProfile.Parse(Tuf9800)!;
        Assert.Equal(new[] { "CPU Ryzen 7 9800X3D", "плата ASUS TUF GAMING B650M-PLUS", "память 2×32 ГБ @ 6000" },
            HwProfile.Similarity(a, a with { MemoryParts = "другой" }));
    }

    [Fact]
    public void Similarity_MemoryNeedsWholeProfile_NotJustFrequency()
    {
        // Решение плана: одна частота 6000 есть почти в каждой сборке — бейдж горел бы у всех.
        var a = HwProfile.Parse(Tuf9800)!;
        var b = new HwProfile("AMD Ryzen 5 7500F 6-Core Processor", "Micro-Star International Co., Ltd.", "MAG B850 TOMAHAWK", 2, 32, 6000, "x");
        Assert.Empty(HwProfile.Similarity(a, b));
        Assert.Equal(new[] { "память 2×16 ГБ @ 6000" }, HwProfile.Similarity(b, b with { Cpu = "i5-12400F", Board = "другая" }));
    }

    [Fact]
    public void Similarity_EmptyFieldsNeverMatch()
    {
        var empty = new HwProfile("", "", "", 0, 0, 0, "");
        Assert.Empty(HwProfile.Similarity(empty, empty));
    }
}
