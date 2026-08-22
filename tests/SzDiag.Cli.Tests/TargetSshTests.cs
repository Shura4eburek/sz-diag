using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>`szcli target` печатал `ssh svc-diag@ip` — команда не подключается: нет `-i` и
/// опций host-ключа (он у каждой сессии свой). Рабочую строку собирали три попытки и поиск
/// по диску (бэклог п.118).</summary>
public class TargetSshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"szkey-{Guid.NewGuid():N}");

    [Fact]
    public void BuildSshLine_WithKey_IsCopyPasteReady()
    {
        var line = TargetSsh.BuildSshLine("svc-diag", "192.168.94.89", @"C:\repo\secrets\svc_diag_key");

        Assert.Contains(@"-i ""C:\repo\secrets\svc_diag_key""", line);
        Assert.Contains("-o StrictHostKeyChecking=no", line);
        Assert.Contains("-o UserKnownHostsFile=NUL", line);
        Assert.EndsWith("svc-diag@192.168.94.89", line);
    }

    [Fact]
    public void BuildSshLine_WithoutKey_FallsBackToPlainSsh()
    {
        var line = TargetSsh.BuildSshLine("svc-diag", "192.168.94.89", null);

        Assert.DoesNotContain("-i ", line);
        Assert.Contains("svc-diag@192.168.94.89", line);
    }

    [Fact]
    public void FindKey_PrefersConfiguredPath()
    {
        Directory.CreateDirectory(_dir);
        var configured = Path.Combine(_dir, "my_key");
        File.WriteAllText(configured, "k");

        Assert.Equal(configured, TargetSsh.FindKey(configured, _dir));
    }

    [Fact]
    public void FindKey_FallsBackToSecretsNextToBaseDir()
    {
        // Ключ лежит в secrets\ корня репозитория; CLI запускается из подпапки —
        // должен найти его сам, а не заставлять искать по диску.
        var secrets = Path.Combine(_dir, "secrets");
        Directory.CreateDirectory(secrets);
        var key = Path.Combine(secrets, "svc_diag_key");
        File.WriteAllText(key, "k");
        var baseDir = Path.Combine(_dir, "dist", "host", "cli");
        Directory.CreateDirectory(baseDir);

        Assert.Equal(key, TargetSsh.FindKey(configured: "", baseDir));
    }

    [Fact]
    public void FindKey_NothingFound_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);

        Assert.Null(TargetSsh.FindKey(configured: "", _dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
