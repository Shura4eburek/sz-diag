using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class PortableSshServerTests
{
    [Fact]
    public void BuildConfig_ContainsPortHostKeyAndAuthorizedKeys()
    {
        var cfg = PortableSshServer.BuildConfig(
            port: 2222,
            hostKeyPath: @"C:\ProgramData\szdiag\ssh\ssh_host_ed25519_key",
            authorizedKeysPath: @"C:\ProgramData\szdiag\ssh\authorized_keys");

        Assert.Contains("Port 2222", cfg);
        Assert.Contains(@"HostKey C:\ProgramData\szdiag\ssh\ssh_host_ed25519_key", cfg);
        // Для админ-аккаунтов Windows OpenSSH иначе форсит administrators_authorized_keys —
        // нам нужен Match-override на нашу папку.
        Assert.Contains("Match Group administrators", cfg);
        Assert.Contains(@"AuthorizedKeysFile C:\ProgramData\szdiag\ssh\authorized_keys", cfg);
    }

    [Fact]
    public void BuildConfig_EnablesVerboseLoggingForDiagnostics()
    {
        var cfg = PortableSshServer.BuildConfig(22, "k", "a");
        Assert.Contains("LogLevel VERBOSE", cfg);
    }

    [Fact]
    public void DescribeFailure_ReturnsLastMeaningfulLogLines()
    {
        var log = string.Join('\n', new[]
        {
            "debug1: sshd version OpenSSH_for_Windows_9.5",
            "debug1: private host key: #0 type 3 ECDSA",
            "Unable to load host key: C:\\...\\ssh_host_ed25519_key",
            "sshd: no hostkeys available -- exiting."
        });

        var msg = PortableSshServer.DescribeFailure(log);

        Assert.Contains("no hostkeys available", msg);
        Assert.DoesNotContain("debug1: sshd version", msg); // шумные debug-строки отброшены
    }

    [Fact]
    public void DescribeFailure_EmptyLog_ReturnsFallback()
    {
        Assert.Contains("без вывода", PortableSshServer.DescribeFailure(""));
    }

    [Fact]
    public void BuildRegisterTaskCommand_RunsSshdUnderSystem()
    {
        var cmd = PortableSshServer.BuildRegisterTaskCommand(
            taskName: "szdiag-sshd-156864",
            sshdExePath: @"C:\dist\client\ssh\sshd.exe",
            configPath: @"C:\ProgramData\szdiag\ssh\sshd_config",
            logPath: @"C:\ProgramData\szdiag\ssh\sshd.log");

        // Ключевое отличие плана Б: SYSTEM (SeTcbPrivilege для logon-токена), а не дочерний процесс.
        Assert.Contains("-User 'SYSTEM'", cmd);
        Assert.Contains("-RunLevel Highest", cmd);
        Assert.Contains("szdiag-sshd-156864", cmd);
        Assert.Contains(@"C:\dist\client\ssh\sshd.exe", cmd);
        Assert.Contains(@"-f ""C:\ProgramData\szdiag\ssh\sshd_config"" -D -E", cmd);
        Assert.Contains("Start-ScheduledTask", cmd);
    }

    [Fact]
    public void BuildHardenAclCommand_LocksToSystemAndAdminsAndSetsOwner()
    {
        var cmd = PortableSshServer.BuildHardenAclCommand(
            @"C:\ProgramData\szdiag\ssh\ssh_host_ed25519_key", @"MAMORU\ENDI");

        // Владелец → Administrators (sshd под SYSTEM не принимает файл, чьим владельцем
        // остаётся обычный юзер-создатель).
        Assert.Contains(@"/setowner '*S-1-5-32-544'", cmd);
        // Снять наследование и убрать явную ACE создателя (её /inheritance:r не трогает).
        Assert.Contains("/inheritance:r", cmd);
        Assert.Contains(@"/remove:g 'MAMORU\ENDI'", cmd);
        // Оставить только SYSTEM (S-1-5-18) и Administrators (S-1-5-32-544), Full.
        Assert.Contains(@"/grant:r '*S-1-5-18:F' '*S-1-5-32-544:F'", cmd);
    }

    [Fact]
    public void BuildStopCommand_UnregistersTaskAndTargetsOwnSshdOnly()
    {
        var cmd = PortableSshServer.BuildStopCommand(
            taskName: "szdiag-sshd-156864",
            configPath: @"C:\ProgramData\szdiag\ssh\sshd_config");

        Assert.Contains("Unregister-ScheduledTask", cmd);
        Assert.Contains("szdiag-sshd-156864", cmd);
        // Добиваем только НАШ sshd — по нашему ConfigPath в командной строке (системный не трогаем).
        Assert.Contains("sshd.exe", cmd);
        Assert.Contains(@"sshd_config", cmd);
        Assert.Contains("Stop-Process", cmd);
    }
}
