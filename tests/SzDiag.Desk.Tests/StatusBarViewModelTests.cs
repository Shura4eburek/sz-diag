using SzDiag.Claude;
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
    public void Healthy_RealVersionString_Shortened()
    {
        // `/api/version` отдаёт готовую строку для `szcli --version` — в статусбар она влезает
        // только без повторного «hub», полного sha и даты сборки.
        var vm = new StatusBarViewModel();
        vm.Apply(HubSnapshot.Empty with { Health = H(), SessionsOkAt = Now,
            HubVersion = "hub 1.0.0+81e1fdb6018bab6053eee6a7a29aa0a7b5816ed5, сборка 2026-07-24 16:10" }, Now);
        Assert.Equal("hub 1.0.0+81e1fdb · ok", vm.HubText);
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

    private static HubStatus St(KbBackupStatus kb, string tunnel = TunnelStates.Off, string? agent = "76a6189")
        => new(agent, kb, new TunnelStatus(tunnel, null));

    [Fact]
    public void Details_NoStatus_Hidden()
    {
        var (text, warn) = StatusBarViewModel.FormatDetails(null);
        Assert.Null(text);
        Assert.False(warn);
    }

    [Fact]
    public void Details_AgentKbTunnel()
    {
        var (text, warn) = StatusBarViewModel.FormatDetails(
            St(new KbBackupStatus(true, Now, "Pushed", "ok"), TunnelStates.Running));
        Assert.StartsWith("агент 76a6189 · kb ", text);
        Assert.Contains("✓", text);
        Assert.EndsWith("· туннель ✓", text);
        Assert.False(warn);
    }

    [Theory]
    [InlineData(false, null, "kb: бэкап выключен", false)]
    [InlineData(true, null, "kb: бэкапа ещё не было", false)]
    [InlineData(true, "CommittedNotPushed", "kb: не выгружен в remote", true)]
    [InlineData(true, "Failed", "kb: бэкап упал", true)]
    public void Details_KbStates(bool enabled, string? outcome, string expected, bool expectedWarn)
    {
        var (text, warn) = StatusBarViewModel.FormatDetails(
            St(new KbBackupStatus(enabled, outcome is null ? null : Now, outcome, null), agent: null));
        Assert.Equal(expected, text);
        Assert.Equal(expectedWarn, warn);
    }

    [Fact]
    public void Details_TunnelDown_Warns()
    {
        var (text, warn) = StatusBarViewModel.FormatDetails(
            St(new KbBackupStatus(false, null, null, null), TunnelStates.Restarting, agent: null));
        Assert.EndsWith("туннель ✗ перезапуск", text);
        Assert.True(warn);
    }

    [Fact]
    public void Apply_SetsDetails()
    {
        var vm = new StatusBarViewModel();
        vm.Apply(HubSnapshot.Empty with { SessionsOkAt = Now, HubVersion = "1.14",
            Status = St(new KbBackupStatus(false, null, null, null)) }, Now);
        Assert.Equal("агент 76a6189 · kb: бэкап выключен", vm.DetailsText);
    }

    private static readonly DateTimeOffset LimNow = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static RateLimitInfo Lim(double fiveHour, double week)
        => new("allowed", new RateLimitWindow(fiveHour, LimNow.AddHours(2)), new RateLimitWindow(week, LimNow.AddDays(3)));

    [Fact]
    public void Limits_PerProfile_PercentAndReset()
    {
        var (text, warn, _) = StatusBarViewModel.FormatLimits(new Dictionary<string, LimitsEntry>
        {
            ["claude2"] = new(Lim(0.09, 0.57), LimNow),
        }, LimNow);
        Assert.StartsWith("claude2 · 5ч 9% до ", text);
        Assert.EndsWith(" · 7д 57%", text);
        Assert.False(warn);
    }

    [Fact]
    public void Limits_80Percent_Warn_AndResetWindowShownAsReset()
    {
        var (text, warn, _) = StatusBarViewModel.FormatLimits(new Dictionary<string, LimitsEntry>
        {
            ["claude"] = new(Lim(0.85, 0.2), LimNow),
        }, LimNow);
        Assert.True(warn);
        Assert.Contains("5ч 85%", text);

        // Окно уже сбросилось, а новых данных не было — старый процент врал бы.
        var (later, _, _) = StatusBarViewModel.FormatLimits(new Dictionary<string, LimitsEntry>
        {
            ["claude"] = new(Lim(0.85, 0.2), LimNow),
        }, LimNow.AddHours(3));
        Assert.Contains("5ч сброшен", later);
    }

    [Fact]
    public void Limits_None_Hidden()
        => Assert.Null(StatusBarViewModel.FormatLimits(new Dictionary<string, LimitsEntry>(), LimNow).Text);
}
