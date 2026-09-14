using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Сам подъём туннеля ходит в систему и юнит-тестами не покрывается (как
/// PortableSshServer) — его проверяет живой прогон. Проверяются команды, которые уезжают
/// в PowerShell: именно в них на живых заявках ломались кавычки и пробелы в путях.</summary>
public class CloudflaredTunnelTests
{
    [Fact]
    public void Задача_регистрируется_под_SYSTEM()
    {
        // Под нагрузкой SSH-сессия рвётся, и обычный дочерний процесс умирает вместе с ней —
        // туннель обязан жить транзиентной задачей, как sshd.
        var cmd = CloudflaredTunnel.BuildRegisterTaskCommand("szdiag-cfd-162003",
            @"C:\tools\cloudflared.exe", 2222, @"C:\work\cloudflared.log");

        Assert.Contains("-User 'SYSTEM'", cmd);
        Assert.Contains("-RunLevel Highest", cmd);
        Assert.Contains("Start-ScheduledTask -TaskName 'szdiag-cfd-162003'", cmd);
    }

    [Fact]
    public void Команда_публикует_именно_ssh_порт()
    {
        var cmd = CloudflaredTunnel.BuildRegisterTaskCommand("t", @"C:\tools\cloudflared.exe",
            2222, @"C:\work\cloudflared.log");

        Assert.Contains("tunnel --url ssh://127.0.0.1:2222", cmd);
        Assert.Contains("--no-autoupdate", cmd);
    }

    [Fact]
    public void Вывод_перенаправлен_в_лог()
    {
        // Имя туннеля cloudflared печатает только в поток — без перенаправления его негде взять.
        var cmd = CloudflaredTunnel.BuildRegisterTaskCommand("t", @"C:\tools\cloudflared.exe",
            2222, @"C:\work\cloudflared.log");

        Assert.Contains(@"> ""C:\work\cloudflared.log"" 2>&1", cmd);
    }

    [Fact]
    public void Путь_с_пробелами_остаётся_в_кавычках()
    {
        // На живых заявках уже ломались на пробелах в пути (см. NativeAgentRestart).
        var cmd = CloudflaredTunnel.BuildRegisterTaskCommand("t",
            @"C:\Program Files\szdiag\cloudflared.exe", 2222, @"C:\work dir\cf.log");

        Assert.Contains(@"""C:\Program Files\szdiag\cloudflared.exe""", cmd);
        Assert.Contains(@"""C:\work dir\cf.log""", cmd);
    }

    [Fact]
    public void Stop_снимает_задачу_и_добивает_процесс()
    {
        var cmd = CloudflaredTunnel.BuildStopCommand("szdiag-cfd-162003", @"C:\tools\cloudflared.exe");

        Assert.Contains("Stop-ScheduledTask -TaskName 'szdiag-cfd-162003'", cmd);
        Assert.Contains("Unregister-ScheduledTask", cmd);
        Assert.Contains("Name='cloudflared.exe'", cmd);
    }

    [Fact]
    public void Stop_не_трогает_чужой_cloudflared()
    {
        // На машине может крутиться свой cloudflared клиента — добиваем только по нашему пути.
        var cmd = CloudflaredTunnel.BuildStopCommand("t", @"C:\tools\cloudflared.exe");

        Assert.Contains(@"C:\\tools\\cloudflared.exe", cmd);
        Assert.Contains("$_.CommandLine -match", cmd);
    }

    [Fact]
    public void Апостроф_в_пути_не_обрывает_PS_литерал()
    {
        // C:\Users\O'Brien\... — тот же приём, что в BuildStopIsolatedJobCommand.
        var cmd = CloudflaredTunnel.BuildStopCommand("t", @"C:\Users\O'Brien\cloudflared.exe");

        Assert.Contains("O''Brien", cmd);
    }

    [Fact]
    public void Имя_туннеля_не_подхватывается_из_прошлого_лога()
    {
        // Start сносит лог перед запуском: иначе hub получил бы адрес мёртвого туннеля —
        // это хуже, чем честное «туннель не поднялся».
        var dir = Path.Combine(Path.GetTempPath(), "szdiag-cfd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tunnel = new CloudflaredTunnel(@"C:\tools\cloudflared.exe", dir, new ФейкPs());
            File.WriteAllText(tunnel.LogPath, "INF |  https://старое-мёртвое.trycloudflare.com  |");

            var host = tunnel.Start(2222, "szdiag-cfd-test", TimeSpan.FromMilliseconds(1));

            Assert.Null(host);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class ФейкPs : IPowerShellRunner
    {
        public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
            => new(0, "", "");
    }
}
