using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>#164: `szcli app restart <СЗ> signalrgb` должен знать, что гасить/поднимать —
/// без пересборки той же ad-hoc последовательности SSH-команд на каждой заявке.</summary>
public class AppCommandTests
{
    [Fact]
    public void KnownApps_HasSignalRgb()
        => Assert.True(AppCommand.KnownApps.ContainsKey("signalrgb"));

    [Fact]
    public void KnownApps_SignalRgb_IsCaseInsensitiveLookup()
        => Assert.True(AppCommand.KnownApps.TryGetValue("SignalRGB", out _));

    [Fact]
    public void KnownApps_SignalRgb_NeedsElevationForScm()
    {
        // OpenSCManager Error 5 без /rl highest — родная боль SignalRGB (бэклог, пункт без номера).
        Assert.True(AppCommand.KnownApps["signalrgb"].Elevated);
    }

    [Fact]
    public void KnownApps_SignalRgb_HasProcessMaskAndService()
    {
        var app = AppCommand.KnownApps["signalrgb"];
        Assert.Equal("SignalRgb*", app.ProcessMask);
        Assert.False(string.IsNullOrWhiteSpace(app.ServiceName));
        Assert.False(string.IsNullOrWhiteSpace(app.LauncherPath));
    }
}
