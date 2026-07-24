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

    [Fact]
    public void Revert_WithAutostartTask_UnregistersItBeforeWatchdog()
    {
        var ps = new FakePs();
        var state = new RevertState
        {
            Sz = "156864",
            AutostartTaskName = "szdiag-autostart-156864", CreatedAutostartTask = true,
            WatchdogTaskName = "szdiag-watchdog-156864", CreatedWatchdogTask = true
        };

        Make(ps).Revert(state);

        var autostartIdx = ps.Scripts.FindIndex(s => s.Contains("szdiag-autostart-156864"));
        var watchdogIdx = ps.Scripts.FindIndex(s => s.Contains("szdiag-watchdog-156864"));
        Assert.True(autostartIdx >= 0, "автостарт-таск должен сниматься");
        Assert.True(autostartIdx < watchdogIdx, "автостарт снимается ПЕРЕД watchdog");
    }

    [Fact]
    public void Revert_WithoutAutostartFlag_DoesNotTouchAutostart()
    {
        var ps = new FakePs();
        var state = new RevertState
        {
            Sz = "156864",
            AutostartTaskName = "szdiag-autostart-156864", CreatedAutostartTask = false
        };

        Make(ps).Revert(state);

        Assert.DoesNotContain(ps.Scripts, s => s.Contains("szdiag-autostart-156864"));
    }

    [Fact]
    public void RevertStaleState_DifferentSz_RevertsOld()
    {
        var ps = new FakePs();
        var mgr = Make(ps);
        RevertStateStore.Save(_statePath, new RevertState
        {
            Sz = "111", AutostartTaskName = "szdiag-autostart-111", CreatedAutostartTask = true
        });

        mgr.RevertStaleState("222");

        Assert.Contains(ps.Scripts, s => s.Contains("Unregister-ScheduledTask") && s.Contains("szdiag-autostart-111"));
    }

    [Fact]
    public void RevertStaleState_SameSz_DoesNothing()
    {
        var ps = new FakePs();
        var mgr = Make(ps);
        RevertStateStore.Save(_statePath, new RevertState { Sz = "222", CreatedUser = true });

        mgr.RevertStaleState("222");

        Assert.Empty(ps.Scripts);
    }

    [Fact]
    public void BuildWatchdogTaskCommand_UsesRevertAndOnceTrigger()
    {
        var cmd = WindowsSystemAccessManager.BuildWatchdogTaskCommand(
            "szdiag-watchdog-156864", @"C:\a\agent.exe", @"C:\s\state.json",
            new DateTime(2026, 7, 24, 15, 0, 0));

        Assert.Contains("--revert", cmd);
        Assert.Contains("-Once", cmd);
        Assert.Contains("2026-07-24T15:00:00", cmd);
        Assert.Contains("szdiag-watchdog-156864", cmd);
        Assert.Contains("SYSTEM", cmd);
    }

    [Fact]
    public void BuildAutostartTaskCommand_UsesAtStartupAndResume()
    {
        var cmd = WindowsSystemAccessManager.BuildAutostartTaskCommand(
            "szdiag-autostart-156864", @"C:\a\agent.exe", @"C:\s\state.json");

        Assert.Contains("-AtStartup", cmd);
        Assert.Contains("--resume", cmd);
        Assert.Contains("szdiag-autostart-156864", cmd);
        Assert.Contains("SYSTEM", cmd);
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_statePath)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
