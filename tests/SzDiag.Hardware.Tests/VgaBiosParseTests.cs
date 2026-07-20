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
}
