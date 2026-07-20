using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class WindowsSystemAccessManagerTests : IDisposable
{
    private readonly string _statePath =
        Path.Combine(Path.GetTempPath(), $"szstate-{Guid.NewGuid():N}", "state.json");

    /// <summary>Фейк: записывает выполненные PowerShell-скрипты, ничего не делает.</summary>
    private sealed class FakePs : IPowerShellRunner
    {
        public List<string> Scripts { get; } = new();
        public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
        {
            Scripts.Add(script);
            return new PsResult(0, "", "");
        }
    }

    private WindowsSystemAccessManager Make(FakePs ps)
    {
        // Реальный PortableSshServer, но workdir не существует (Revert его пропустит) —
        // важно лишь, что Stop зовёт _ps.Run(BuildStopCommand) с нашим SshdTaskName.
        var sshd = new PortableSshServer(@"C:\nonexistent\ssh",
            Path.Combine(Path.GetTempPath(), $"szwork-{Guid.NewGuid():N}"), ps);
        return new WindowsSystemAccessManager(ps, sshd, _statePath);
    }

    [Fact]
    public void Revert_WithSshdTask_StopsSshdUnderItsFlag()
    {
        var ps = new FakePs();
        var state = new RevertState { Sz = "156864", SshdTaskName = "szdiag-sshd-156864", CreatedSshdTask = true };

        Make(ps).Revert(state);

        Assert.Contains(ps.Scripts, s => s.Contains("Unregister-ScheduledTask") && s.Contains("szdiag-sshd-156864"));
    }

    [Fact]
    public void Revert_WithoutSshdFlag_DoesNotStopSshd()
    {
        var ps = new FakePs();
        var state = new RevertState { Sz = "156864", SshdTaskName = "szdiag-sshd-156864", CreatedSshdTask = false };

        Make(ps).Revert(state);

        Assert.DoesNotContain(ps.Scripts, s => s.Contains("szdiag-sshd-156864"));
    }

    [Fact]
    public void Revert_Twice_IsIdempotent()
    {
        var ps = new FakePs();
        var state = new RevertState { Sz = "156864", SshdTaskName = "szdiag-sshd-156864", CreatedSshdTask = true };

        var mgr = Make(ps);
        mgr.Revert(state);
        var ex = Record.Exception(() => mgr.Revert(state)); // повторный откат безопасен

        Assert.Null(ex);
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_statePath)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
