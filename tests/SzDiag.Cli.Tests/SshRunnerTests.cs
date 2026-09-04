using System.Text;
using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>#160/бэклог п.206: `agent restart --ssh` — путь в обход exec-канала, когда он
/// мёртв при живом heartbeat (СЗ 162367). Сборка команды и кодирование — чистые функции.</summary>
public class SshRunnerTests
{
    [Fact]
    public void EncodeCommand_RoundTrips_AsUtf16Base64()
    {
        var script = "Write-Output 'привет'";
        var encoded = SshRunner.EncodeCommand(script);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.Equal(script, decoded);
    }

    [Fact]
    public void BuildArgs_WithKey_IncludesKeyAndTarget()
    {
        var args = SshRunner.BuildArgs("svc-diag", "192.168.1.50", @"C:\secrets\svc_diag_key", "Write-Output 1");

        Assert.Contains("-i", args);
        Assert.Contains(@"C:\secrets\svc_diag_key", args);
        Assert.Contains("svc-diag@192.168.1.50", args);
    }

    [Fact]
    public void BuildArgs_WithoutKey_OmitsKeyFlag()
    {
        var args = SshRunner.BuildArgs("svc-diag", "192.168.1.50", null, "Write-Output 1");

        Assert.DoesNotContain("-i", args);
    }

    [Fact]
    public void BuildArgs_DisablesHostKeyChecking()
    {
        var args = SshRunner.BuildArgs("svc-diag", "1.2.3.4", null, "Write-Output 1");

        Assert.Contains("StrictHostKeyChecking=no", args);
    }

    [Fact]
    public void BuildArgs_LastArgRunsEncodedCommand()
    {
        var args = SshRunner.BuildArgs("svc-diag", "1.2.3.4", null, "Write-Output 1");

        Assert.Contains("-EncodedCommand", args[^1]);
    }
}
