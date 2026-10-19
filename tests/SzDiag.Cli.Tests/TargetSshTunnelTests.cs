using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Туннельный режим: у машины нет входящего порта, доступного хосту, и подключение
/// идёт через `cloudflared access ssh`. Строка обязана оставаться копипастной — её вставляют
/// в терминал как есть.</summary>
public class TargetSshTunnelTests
{
    [Fact]
    public void Прямой_режим_строит_обычную_строку()
    {
        var line = TargetSsh.BuildSshLine("svc-diag", "192.168.1.5", @"C:\keys\svc_diag_key",
            viaTunnel: false);

        Assert.Contains("svc-diag@192.168.1.5", line);
        Assert.DoesNotContain("ProxyCommand", line);
    }

    [Fact]
    public void Туннельный_режим_добавляет_ProxyCommand()
    {
        var line = TargetSsh.BuildSshLine("svc-diag", "aaa-bbb.trycloudflare.com",
            @"C:\keys\svc_diag_key", viaTunnel: true);

        Assert.Contains("cloudflared access ssh --hostname %h", line);
        Assert.Contains("svc-diag@aaa-bbb.trycloudflare.com", line);
    }

    [Fact]
    public void ProxyCommand_взят_в_кавычки_целиком()
    {
        // Без кавычек ssh считает командой только первое слово, а остальное разбирает как
        // свои аргументы — строку нельзя было бы просто скопировать и вставить.
        var line = TargetSsh.BuildSshLine("svc-diag", "aaa-bbb.trycloudflare.com", null,
            viaTunnel: true);

        Assert.Contains("-o \"ProxyCommand cloudflared access ssh --hostname %h\"", line);
    }

    [Fact]
    public void Известный_ключ_включает_строгую_проверку()
    {
        var line = TargetSsh.BuildSshLine("svc-diag", "aaa-bbb.trycloudflare.com", null,
            viaTunnel: true, knownHostsPath: @"C:\hub\known_hosts\162003");

        Assert.Contains("StrictHostKeyChecking=yes", line);
        Assert.Contains(@"UserKnownHostsFile=""C:\hub\known_hosts\162003""", line);
        Assert.DoesNotContain("StrictHostKeyChecking=no", line);
    }

    [Fact]
    public void Без_известного_ключа_остаётся_прежнее_поведение()
    {
        // Пока ключ не приехал (агент старой сборки), ломать рабочий путь нельзя.
        var line = TargetSsh.BuildSshLine("svc-diag", "192.168.1.5", null, viaTunnel: false);

        Assert.Contains("StrictHostKeyChecking=no", line);
        Assert.Contains("UserKnownHostsFile=NUL", line);
    }

    [Fact]
    public void Старая_сигнатура_из_трёх_аргументов_продолжает_работать()
    {
        // Вызывающие в Program.cs не должны править ради туннеля.
        var line = TargetSsh.BuildSshLine("svc-diag", "192.168.1.5", null);

        Assert.Contains("svc-diag@192.168.1.5", line);
        Assert.DoesNotContain("ProxyCommand", line);
    }
}
