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

    // review W2 T-6: критерий #164 требует «в выводе PID и статус службы, задача удалена» —
    // ветка `restart` раньше не была покрыта тестами вовсе (вся последовательность
    // Stop-Process/Start-Service собиралась инлайн в приватном RestartAppAsync). Вынесенный
    // билдер BuildStopAndStartServiceScript проверяем как чистую функцию; RunLevel/PID/
    // Unregister-ScheduledTask из общего InteractiveSessionRun.BuildScript уже покрыты
    // InteractiveSessionRunTests — restart переиспользует тот же билдер для запуска лаунчера.
    [Fact]
    public void BuildStopAndStartServiceScript_StopsProcessByMask()
    {
        var script = AppCommand.BuildStopAndStartServiceScript(AppCommand.KnownApps["signalrgb"]);

        Assert.Contains("Get-Process -Name 'SignalRgb*'", script);
        Assert.Contains("Stop-Process -Force", script);
    }

    [Fact]
    public void BuildStopAndStartServiceScript_StartsKnownService()
    {
        var script = AppCommand.BuildStopAndStartServiceScript(AppCommand.KnownApps["signalrgb"]);

        Assert.Contains("Start-Service -Name 'SignalRgb.Service'", script);
    }

    [Fact]
    public void BuildStopAndStartServiceScript_ReportsServiceStatusEitherWay()
    {
        // Критерий #164: в выводе обязан быть статус службы — успех и провал оба формулируют
        // явную строку, а не проглатывают ошибку молча.
        var script = AppCommand.BuildStopAndStartServiceScript(AppCommand.KnownApps["signalrgb"]);

        Assert.Contains("служба SignalRgb.Service: запущена", script);
        Assert.Contains("catch", script);
        Assert.Contains("служба SignalRgb.Service: ", script); // ветка catch тоже называет службу
    }

    [Fact]
    public void BuildStopAndStartServiceScript_UsesGenericAppRecord_NotHardcodedToSignalRgb()
    {
        // Билдер обязан работать для ЛЮБОГО KnownApp, не только для сегодняшнего единственного
        // (расширяется по мере заявок — как и весь остальной словарь).
        var custom = new AppCommand.KnownApp("Foo*", "FooSvc", @"C:\Foo\foo.exe", Elevated: false);

        var script = AppCommand.BuildStopAndStartServiceScript(custom);

        Assert.Contains("Get-Process -Name 'Foo*'", script);
        Assert.Contains("Start-Service -Name 'FooSvc'", script);
    }
}
