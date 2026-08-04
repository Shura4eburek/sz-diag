using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class StickyCapabilitiesTests
{
    [Fact]
    public void Evaluate_AllGood_ReturnsTrue()
    {
        var r = StickyCapabilities.Evaluate(outputRedirected: false, vtEnabled: true,
            windowHeight: 30, configEnabled: true);
        Assert.True(r.Enabled);
    }

    [Fact]
    public void Evaluate_OutputRedirected_Disabled()
    {
        var r = StickyCapabilities.Evaluate(outputRedirected: true, vtEnabled: true,
            windowHeight: 30, configEnabled: true);
        Assert.False(r.Enabled);
        Assert.Contains("перенаправлен", r.Reason);
    }

    [Fact]
    public void Evaluate_NoVt_Disabled()
    {
        var r = StickyCapabilities.Evaluate(outputRedirected: false, vtEnabled: false,
            windowHeight: 30, configEnabled: true);
        Assert.False(r.Enabled);
        Assert.Contains("VT", r.Reason);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(50, true)]
    public void Evaluate_HeightThresholdIsTen(int height, bool expected)
    {
        var r = StickyCapabilities.Evaluate(outputRedirected: false, vtEnabled: true,
            windowHeight: height, configEnabled: true);
        Assert.Equal(expected, r.Enabled);
    }

    [Fact]
    public void Evaluate_ConfigDisabled_Disabled()
    {
        var r = StickyCapabilities.Evaluate(outputRedirected: false, vtEnabled: true,
            windowHeight: 30, configEnabled: false);
        Assert.False(r.Enabled);
        Assert.Contains("конфиг", r.Reason);
    }
}
