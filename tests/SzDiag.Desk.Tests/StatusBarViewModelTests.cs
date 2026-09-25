using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class StatusBarViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static HealthzResponse H(long pending = 0) => new(30, 32000, 32767, 1000, 1000, pending, Now);

    [Fact]
    public void Healthy_ShowsVersion()
    {
        var vm = new StatusBarViewModel();
        vm.Apply(HubSnapshot.Empty with { Health = H(), HubVersion = "1.14", SessionsOkAt = Now }, Now);
        Assert.True(vm.HubOk);
        Assert.Equal("hub 1.14 · ok", vm.HubText);
        Assert.Null(vm.StaleText);
    }

    [Fact]
    public void Stale_ShowsLastDataTime()
    {
        var vm = new StatusBarViewModel();
        vm.Apply(HubSnapshot.Empty with { Error = "hub не отвечает: refused", Failures = 2,
            SessionsOkAt = new DateTimeOffset(2026, 9, 25, 11, 58, 0, TimeSpan.Zero) }, Now);
        Assert.False(vm.HubOk);
        Assert.Equal("hub не отвечает", vm.HubText);
        Assert.StartsWith("данные на ", vm.StaleText);
    }

    [Fact]
    public void Starvation_IsNotOk()
    {
        // Очередь thread pool растёт — hub жив, но захлёбывается (бэклог п.50).
        var vm = new StatusBarViewModel();
        vm.Apply(HubSnapshot.Empty with { Health = H(pending: 500), HubVersion = "1.14", SessionsOkAt = Now }, Now);
        Assert.False(vm.HubOk);
        Assert.Contains("очередь 500", vm.HubText);
    }
}
