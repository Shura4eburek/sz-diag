using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Выбор режима доступа вынесен из Program.cs отдельно, чтобы правило было
/// проверяемым: ошибка здесь означает, что `szcli target` печатает строку, которая не
/// сработает, — а это ровно то враньё, которое запрещено правилом честности отчётов.</summary>
public class AccessReporterTests
{
    [Fact]
    public void Туннель_поднят_режим_туннельный()
    {
        var state = new RevertState
        {
            Sz = "162003",
            StartedQuickTunnel = true,
            QuickTunnelHost = "aaa-bbb.trycloudflare.com",
        };

        var report = AccessReporter.BuildReport(state, "162003", foundHubByBroadcast: false);

        Assert.Equal(AccessMode.Tunnel, report.AccessMode);
        Assert.Equal("aaa-bbb.trycloudflare.com", report.AccessHost);
    }

    [Fact]
    public void Hub_найден_broadcast_ом_режим_прямой()
    {
        // Машина в одной сети с боксом: прямой SSH быстрее и не зависит от Cloudflare.
        var state = new RevertState
        {
            Sz = "162003",
            StartedQuickTunnel = true,
            QuickTunnelHost = "aaa-bbb.trycloudflare.com",
        };

        var report = AccessReporter.BuildReport(state, "162003", foundHubByBroadcast: true);

        Assert.Equal(AccessMode.Direct, report.AccessMode);
        Assert.Null(report.AccessHost);
    }

    [Fact]
    public void Туннель_не_поднялся_но_hub_по_домену_режим_всё_равно_прямой()
    {
        // Честность: раз имени нет, туннельным режим называть нельзя — иначе target
        // напечатает ProxyCommand в никуда.
        var state = new RevertState { Sz = "162003", StartedQuickTunnel = false };

        var report = AccessReporter.BuildReport(state, "162003", foundHubByBroadcast: false);

        Assert.Equal(AccessMode.Direct, report.AccessMode);
        Assert.Null(report.AccessHost);
    }

    [Fact]
    public void Флаг_стоит_а_имя_пустое_считаем_туннеля_нет()
    {
        // Рассогласованное состояние (например, откат прошёл наполовину) не должно
        // превращаться в адрес пустой строкой.
        var state = new RevertState { Sz = "162003", StartedQuickTunnel = true, QuickTunnelHost = "" };

        var report = AccessReporter.BuildReport(state, "162003", foundHubByBroadcast: false);

        Assert.Equal(AccessMode.Direct, report.AccessMode);
        Assert.Null(report.AccessHost);
    }

    [Fact]
    public void Номер_СЗ_берётся_из_аргумента_а_не_из_состояния()
    {
        // Состояние могло остаться от прошлой заявки — отчитываемся за ту СЗ, под которой
        // реально зарегистрированы.
        var state = new RevertState { Sz = "160705", StartedQuickTunnel = false };

        var report = AccessReporter.BuildReport(state, "162003", foundHubByBroadcast: true);

        Assert.Equal("162003", report.Sz);
    }
}
