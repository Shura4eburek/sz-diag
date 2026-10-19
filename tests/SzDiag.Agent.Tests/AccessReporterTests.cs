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
    public void Host_ключ_едет_в_отчёте_в_обоих_режимах()
    {
        // Пиннинг нужен и в прямом режиме: IP переиспользуются между заявками, и подмену
        // там сегодня тоже нечем отличить.
        var прямой = new RevertState { Sz = "162003", SshHostPublicKey = "ssh-ed25519 AAAA1" };
        var туннельный = new RevertState
        {
            Sz = "162003",
            StartedQuickTunnel = true,
            QuickTunnelHost = "a.trycloudflare.com",
            SshHostPublicKey = "ssh-ed25519 AAAA1",
        };

        Assert.Equal("ssh-ed25519 AAAA1",
            AccessReporter.BuildReport(прямой, "162003", true).SshHostKeyFingerprint);
        Assert.Equal("ssh-ed25519 AAAA1",
            AccessReporter.BuildReport(туннельный, "162003", false).SshHostKeyFingerprint);
    }

    [Fact]
    public void Комментарий_из_pub_файла_в_known_hosts_не_едет()
    {
        // Формат *.pub — «тип ключ комментарий». Комментарий в known_hosts не нужен и
        // только мешает сравнению.
        var dir = Path.Combine(Path.GetTempPath(), "szssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ssh_host_ed25519_key.pub"),
                "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5 szdiag@TEST-PC\n");

            Assert.Equal("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5", SshHostKeyReader.TryRead(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Нет_pub_файла_ключа_нет_и_пиннинг_просто_не_включится()
    {
        Assert.Null(SshHostKeyReader.TryRead(Path.Combine(Path.GetTempPath(), "нет-такой-папки-" + Guid.NewGuid())));
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
