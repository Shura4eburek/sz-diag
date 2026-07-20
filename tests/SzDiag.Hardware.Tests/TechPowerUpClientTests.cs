using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class TechPowerUpClientTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void EnsureNotBlocked_ChallengePage_Throws()
    {
        var html = Fixture("gpu-specs-challenge.html");
        Assert.Throws<ScrapeBlockedException>(() => TechPowerUpClient.EnsureNotBlocked(html));
    }

    [Fact]
    public void EnsureNotBlocked_NormalPage_Passes()
    {
        var html = Fixture("vgabios-detail.html");
        TechPowerUpClient.EnsureNotBlocked(html);   // не должно кинуть
    }
}
