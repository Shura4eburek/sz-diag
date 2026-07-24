using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class RevertStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"szstate-{Guid.NewGuid():N}", "state.json");

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var state = new RevertState
        {
            Sz = "156864",
            CreatedUser = true,
            SetTokenPolicy = true,
            TokenPolicyPreviousValue = null,
            FirewallRuleName = "szdiag-ssh",
            StoppedSystemSshd = true,
            GeneratedHostKeys = true,
            SshdTaskName = "szdiag-sshd-156864",
            CreatedSshdTask = true,
            AutostartTaskName = "szdiag-autostart-156864",
            CreatedAutostartTask = true
        };

        RevertStateStore.Save(_path, state);
        var loaded = RevertStateStore.Load(_path);

        Assert.NotNull(loaded);
        Assert.Equal("156864", loaded!.Sz);
        Assert.True(loaded.CreatedUser);
        Assert.True(loaded.SetTokenPolicy);
        Assert.Null(loaded.TokenPolicyPreviousValue);
        Assert.Equal("szdiag-ssh", loaded.FirewallRuleName);
        Assert.True(loaded.StoppedSystemSshd);
        Assert.True(loaded.GeneratedHostKeys);
        Assert.Equal("szdiag-sshd-156864", loaded.SshdTaskName);
        Assert.True(loaded.CreatedSshdTask);
        Assert.Equal("szdiag-autostart-156864", loaded.AutostartTaskName);
        Assert.True(loaded.CreatedAutostartTask);
    }

    [Fact]
    public void Load_Missing_ReturnsNull() => Assert.Null(RevertStateStore.Load(_path));

    [Fact]
    public void Delete_RemovesFile()
    {
        RevertStateStore.Save(_path, new RevertState());
        RevertStateStore.Delete(_path);
        Assert.False(File.Exists(_path));
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
