using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class VgaBiosParseTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void ParseSearch_ReturnsRowsWithCardNameAndUrl()
    {
        var rows = VgaBiosParser.ParseSearch(Fixture("vgabios-search.html"));

        Assert.NotEmpty(rows);
        var r = rows.First(x => x.DetailUrl.Contains("275654"));
        Assert.Equal("MSI", r.Manufacturer);
        Assert.Equal("Ventus 2x OC Plus", r.CardName);
        Assert.StartsWith("/vgabios/275654", r.DetailUrl);
        Assert.Equal("2025-03-15", r.DateCompiled);        // дата без времени
        Assert.Equal("98.06.1F.00.CD", r.VbiosVersion);
    }

    [Fact]
    public void ParseDetail_ExtractsSubsystemMemoryClocksPower()
    {
        var d = VgaBiosParser.ParseDetail(Fixture("vgabios-detail.html"));

        Assert.Equal("1462", d.SubVendorId);   // нормализовано в lowercase
        Assert.Equal("5351", d.SubDeviceId);
        Assert.Equal("16384 MB", d.MemorySize);
        Assert.Equal("GDDR7", d.MemoryType);
        Assert.Equal("2407 MHz", d.CoreClock);
        Assert.Equal("2602 MHz", d.BoostClock);
        Assert.Equal("1750 MHz", d.MemoryClock);
        Assert.Equal("180.0 W", d.PowerTarget);
        Assert.Equal("180.0 W", d.PowerLimit);
        Assert.Contains("HDMI", d.Outputs);
        Assert.Contains("DisplayPort", d.Outputs);
        Assert.Equal("98.06.1F.00.CD", d.VbiosVersion);
    }
}
