using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Список известных «двойных» демонов для `client info` — цена решения перед тем,
/// как писать «вылечено» (бэклог п.172, СЗ 161190: LEDKeeper2 гасил и подсветку башни/корпуса
/// вместе с P0-троттлингом видеокарты).</summary>
public class DualPurposeSoftwareTests
{
    [Fact]
    public void Known_IsNotEmpty()
        => Assert.NotEmpty(DualPurposeSoftware.Known);

    [Fact]
    public void Known_ContainsLedKeeperCase()
        => Assert.Contains(DualPurposeSoftware.Known,
            e => e.Name.Contains("LEDKeeper2") && e.Controls.Contains("подсветка"));

    [Fact]
    public void Known_EntriesHaveNameAndControls()
    {
        foreach (var entry in DualPurposeSoftware.Known)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.Controls));
        }
    }
}
