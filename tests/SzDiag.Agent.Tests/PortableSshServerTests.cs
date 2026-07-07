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
}
