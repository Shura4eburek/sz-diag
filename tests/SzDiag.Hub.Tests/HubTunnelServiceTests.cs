using Microsoft.Extensions.Logging.Abstractions;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class HubTunnelServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"szhubtun-{Guid.NewGuid():N}");

    [Fact]
    public void BuildArguments_PutsGlobalFlagsBeforeSubcommand()
    {
        var args = HubTunnelService.BuildArguments(new HubTunnelOptions
        {
            ConfigPath = @"C:\Users\x\.cloudflared\szdiag-hub.yml",
            Name = "szdiag-hub",
        });

        Assert.Equal(new[]
        {
            "--config", @"C:\Users\x\.cloudflared\szdiag-hub.yml",
            "--no-autoupdate", "tunnel", "run", "szdiag-hub",
        }, args);
    }

    [Fact]
    public void BuildArguments_OmitsEmptyConfigAndName()
    {
        var args = HubTunnelService.BuildArguments(new HubTunnelOptions());

        Assert.Equal(new[] { "--no-autoupdate", "tunnel", "run" }, args);
    }

    [Fact]
    public void ResolveExecutable_ReturnsConfiguredPathOnlyWhenItExists()
    {
        Assert.Equal(@"D:\cf\cloudflared.exe",
            HubTunnelService.ResolveExecutable(@"D:\cf\cloudflared.exe", p => p == @"D:\cf\cloudflared.exe"));
        Assert.Null(HubTunnelService.ResolveExecutable(@"D:\cf\cloudflared.exe", _ => false));
    }

    [Fact]
    public void ResolveExecutable_FallsBackToStandardInstallPath()
    {
        var standard = @"C:\Program Files (x86)\cloudflared\cloudflared.exe";

        Assert.Equal(standard, HubTunnelService.ResolveExecutable("", p => p == standard));
        Assert.Null(HubTunnelService.ResolveExecutable("", _ => false));
    }

    [Fact]
    public void PidFilePath_RelativeResolvesFromExeFolder()
    {
        var service = new HubTunnelService(
            new HubTunnelOptions { PidFile = "cloudflared.pid" }, NullLogger<HubTunnelService>.Instance, _dir);

        Assert.Equal(Path.Combine(_dir, "cloudflared.pid"), service.PidFilePath);
    }

    [Fact]
    public void PidFilePath_AbsoluteKeptAsIs()
    {
        var abs = Path.Combine(_dir, "sub", "tun.pid");
        var service = new HubTunnelService(
            new HubTunnelOptions { PidFile = abs }, NullLogger<HubTunnelService>.Instance, _dir);

        Assert.Equal(abs, service.PidFilePath);
    }

    [Fact]
    public async Task Disabled_DoesNotTouchPidFileOrProcesses()
    {
        Directory.CreateDirectory(_dir);
        var pidFile = Path.Combine(_dir, "cloudflared.pid");
        File.WriteAllText(pidFile, "424242");   // остаток от прошлого запуска

        var service = new HubTunnelService(
            new HubTunnelOptions { Enabled = false }, NullLogger<HubTunnelService>.Instance, _dir);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(pidFile));   // выключенный сервис ничего не трогает
    }

    [Fact]
    public async Task ExecutableNotFound_StopsQuietlyWithoutPidFile()
    {
        var service = new HubTunnelService(
            new HubTunnelOptions { Enabled = true, ExecutablePath = Path.Combine(_dir, "нет-такого.exe") },
            NullLogger<HubTunnelService>.Instance, _dir);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_dir, "cloudflared.pid")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
