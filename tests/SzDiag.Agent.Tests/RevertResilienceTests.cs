using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Откат обязан доводить дело до конца, даже когда отдельный шаг падает: на 160705
/// одно исключение оставило на клиенте учётку, sshd, правило фаервола и token policy
/// навсегда (бэклог п.59).</summary>
public class RevertResilienceTests : IDisposable
{
    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"szstate-{Guid.NewGuid():N}.json");

    /// <summary>Runner, который падает на командах с заданной подстрокой.</summary>
    private sealed class FlakyRunner : IPowerShellRunner
    {
        private readonly string _failOn;
        public List<string> Ran { get; } = new();

        public FlakyRunner(string failOn) => _failOn = failOn;

        public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
        {
            Ran.Add(script);
            if (_failOn.Length > 0 && script.Contains(_failOn, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"шаг упал: {_failOn}");
            return new PsResult(0, "", "");
        }
    }

    private sealed class SpySshServer : ISshServer
    {
        public bool Stopped { get; private set; }
        public bool ThrowOnStop { get; init; }
        public string WorkDir => Path.Combine(Path.GetTempPath(), $"szssh-{Guid.NewGuid():N}");
        public void Start(int port, string authorizedKeyLine, string taskName) { }
        public void Stop(string taskName)
        {
            if (ThrowOnStop) throw new UnauthorizedAccessException("sshd не снялся");
            Stopped = true;
        }
    }

    private static RevertState FullState() => new()
    {
        Sz = "160705",
        ServiceAccount = "svc-diag",
        FirewallRuleName = "szdiag-ssh-160705",
        WatchdogTaskName = "szdiag-watchdog-160705",
        SshdTaskName = "szdiag-sshd-160705",
        AutostartTaskName = "szdiag-autostart-160705",
        CreatedUser = true,
        CreatedSshdTask = true,
        AddedFirewallRule = true,
        SetTokenPolicy = true,
        CreatedWatchdogTask = true,
        CreatedAutostartTask = true,
        StoppedSystemSshd = true,
    };

    [Fact]
    public void Revert_StepThrows_OtherStepsStillApplied()
    {
        // Падает снятие фаервола — учётка и всё остальное обязаны сняться всё равно.
        var ps = new FlakyRunner("Remove-NetFirewallRule");
        var manager = new WindowsSystemAccessManager(ps, new SpySshServer(), _statePath);

        var outcome = manager.Revert(FullState());

        Assert.False(outcome.AllClean);
        Assert.Single(outcome.Failed);
        Assert.Equal("правило фаервола", outcome.Failed[0].Step);
        Assert.Contains("учётка svc-diag", outcome.Done);
        Assert.Contains("watchdog-задача", outcome.Done);
        Assert.Contains(ps.Ran, c => c.Contains("Remove-LocalUser"));
    }

    [Fact]
    public void Revert_SshdThrows_RestStillReverted()
    {
        var ps = new FlakyRunner("");
        var manager = new WindowsSystemAccessManager(ps, new SpySshServer { ThrowOnStop = true }, _statePath);

        var outcome = manager.Revert(FullState());

        Assert.Contains(outcome.Failed, f => f.Step == "sshd");
        Assert.Contains("учётка svc-diag", outcome.Done);
        Assert.Contains("правило фаервола", outcome.Done);
    }

    [Fact]
    public void Revert_Failure_KeepsStateFileForRetry()
    {
        // Файл состояния — единственное, по чему повторная попытка узнает, что доделывать.
        File.WriteAllText(_statePath, "{}");
        var manager = new WindowsSystemAccessManager(
            new FlakyRunner("Remove-LocalUser"), new SpySshServer(), _statePath);

        var outcome = manager.Revert(FullState());

        Assert.False(outcome.AllClean);
        Assert.True(File.Exists(_statePath), "при неполном откате состояние сносить нельзя");
    }

    [Fact]
    public void Revert_Clean_DeletesStateFile()
    {
        File.WriteAllText(_statePath, "{}");
        var manager = new WindowsSystemAccessManager(new FlakyRunner(""), new SpySshServer(), _statePath);

        var outcome = manager.Revert(FullState());

        Assert.True(outcome.AllClean);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void Revert_NothingApplied_IsNoOpNotFailure()
    {
        var outcome = new WindowsSystemAccessManager(new FlakyRunner(""), new SpySshServer(), _statePath)
            .Revert(new RevertState { Sz = "160705" });

        Assert.True(outcome.AllClean);
    }

    [Fact]
    public void Summary_FailedSteps_MentionsWhatRemained()
    {
        var outcome = new WindowsSystemAccessManager(
            new FlakyRunner("Remove-LocalUser"), new SpySshServer(), _statePath).Revert(FullState());

        var summary = outcome.Summary();
        Assert.Contains("ЧАСТИЧНО", summary);
        Assert.Contains("учётка svc-diag", summary);
    }

    public void Dispose()
    {
        try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch { }
    }
}

public class CommandChannelWatchdogTests
{
    [Fact]
    public void Observe_SingleFailure_NotEnoughToHeal()
    {
        // Одна неудача — норма: под OCCT дочерний PowerShell стартует минутами (п.35).
        var w = new CommandChannelWatchdog(failuresBeforeHeal: 3);

        Assert.False(w.Observe(false));
        Assert.Equal(1, w.ConsecutiveFailures);
    }

    [Fact]
    public void Observe_ThreeFailuresInRow_SignalsHeal()
    {
        var w = new CommandChannelWatchdog(failuresBeforeHeal: 3);

        Assert.False(w.Observe(false));
        Assert.False(w.Observe(false));
        Assert.True(w.Observe(false));
    }

    [Fact]
    public void Observe_SuccessResetsCounter()
    {
        var w = new CommandChannelWatchdog(failuresBeforeHeal: 3);
        w.Observe(false);
        w.Observe(false);

        Assert.False(w.Observe(true));
        Assert.Equal(0, w.ConsecutiveFailures);
        Assert.False(w.Observe(false));   // счёт пошёл заново
    }

    [Fact]
    public void BuildSelfHealCommand_RunsAutostartTaskAfterDelay()
    {
        // Через задачу и с паузой: новый экземпляр не поднимется, пока живёт старый (мьютекс).
        var cmd = CommandChannelWatchdog.BuildSelfHealCommand("szdiag-autostart-160705");

        Assert.Contains("schtasks /run /tn \"szdiag-autostart-160705\"", cmd);
        Assert.Contains("timeout /t 5", cmd);
    }

    [Fact]
    public void Probe_LivePowerShell_Succeeds()
    {
        Assert.True(CommandChannelWatchdog.Probe(new PowerShellRunner(), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Probe_HangingRunner_ReportsDead()
    {
        var hanging = new HangingRunner();

        Assert.False(CommandChannelWatchdog.Probe(hanging, TimeSpan.FromMilliseconds(50)));
    }

    private sealed class HangingRunner : IPowerShellRunner
    {
        public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
            => throw new PowerShellTimeoutException("канал завис");
    }
}
