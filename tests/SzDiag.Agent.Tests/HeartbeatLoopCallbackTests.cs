using SzDiag.Agent;

namespace SzDiag.Agent.Tests;

public class HeartbeatLoopCallbackTests
{
    /// <summary>Минимальный IHubLink: считает heartbeat'ы и по требованию падает.
    /// Сигнатуры — по src/SzDiag.Agent/IHubLink.cs (все члены обязательны).</summary>
    private sealed class CountingLink : IHubLink
    {
        private readonly bool _throw;
        public int Calls;
        public CountingLink(bool shouldThrow) => _throw = shouldThrow;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterAsync(string sz, string hostname, DateTimeOffset? bootTime = null, string? lastShutdown = null,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task HeartbeatAsync(string sz, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (_throw) throw new InvalidOperationException("канал лёг");
            return Task.CompletedTask;
        }
        public void OnRevert(Func<string, Task> handler) { }
        public void OnRunTests(Func<string, string?, Task> handler) { }
        public void OnRunDiag(Func<string, string?, Task> handler) { }
        public void OnExec(Func<SzDiag.Contracts.ExecRequest, Task> handler) { }
        public Task SendExecResultAsync(SzDiag.Contracts.ExecResult result,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task SendExecAckAsync(SzDiag.Contracts.ExecAck ack, CancellationToken ct = default) => Task.CompletedTask;
        public void OnExecStatus(Func<SzDiag.Contracts.ExecStatusRequest, Task> handler) { }
        public Task SendExecJobStatusAsync(SzDiag.Contracts.ExecJobStatus status, CancellationToken ct = default) => Task.CompletedTask;
        public void OnPush(Func<SzDiag.Contracts.PushRequest, Task> handler) { }
        public Task SendPushResultAsync(SzDiag.Contracts.PushResult result, CancellationToken ct = default) => Task.CompletedTask;
        public void OnPull(Func<SzDiag.Contracts.PullRequest, Task> handler) { }
        public Task SendPullChunkAsync(SzDiag.Contracts.PullChunk chunk,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPullResultAsync(SzDiag.Contracts.PullResult result,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task UploadReportFileAsync(SzDiag.Contracts.UploadReportPart part,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since,
            CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopAccessManager : ISystemAccessManager
    {
        public RevertState Open(AccessSpec spec) => new() { Sz = spec.Sz };
        public RevertOutcome Revert(RevertState state)
            => new(Array.Empty<string>(), Array.Empty<RevertStepFailure>());
        public void Resume(RevertState state, AccessSpec spec) { }
    }

    /// <summary>StartAsync не зовём: HeartbeatOnceAsync дёргает link напрямую.</summary>
    private static AgentSession MakeSession(IHubLink link) =>
        new(new NoopAccessManager(), link,
            new AccessSpec("156864", "svc-diag", "ssh-ed25519 AAAA...", 22, TimeSpan.FromHours(6)),
            "PC-1");

    [Fact]
    public async Task Callback_FiresTrue_OnSuccess()
    {
        var link = new CountingLink(shouldThrow: false);
        var ok = false;
        using var cts = new CancellationTokenSource();
        var loop = AgentCommandWiring.StartHeartbeatLoop(MakeSession(link), 60, cts.Token,
            success => { if (success) ok = true; });

        while (link.Calls == 0) await Task.Delay(5);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }

        Assert.True(ok);
    }

    [Fact]
    public async Task Callback_FiresFalse_OnFailure()
    {
        var link = new CountingLink(shouldThrow: true);
        bool? seen = null;
        using var cts = new CancellationTokenSource();
        var loop = AgentCommandWiring.StartHeartbeatLoop(MakeSession(link), 60, cts.Token,
            success => seen ??= success);

        while (link.Calls == 0) await Task.Delay(5);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }

        Assert.False(seen);
    }

    [Fact]
    public async Task Loop_TouchesLivenessMark_SoWatchdogSeesLiveAgent()
    {
        // Регрессия (п.85): watchdog не знал про агента и срезал доступ под живой сессией.
        var dir = Path.Combine(Path.GetTempPath(), $"szhb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var statePath = Path.Combine(dir, "state.json");
        File.WriteAllText(statePath, "{}");
        try
        {
            var link = new CountingLink(shouldThrow: false);
            using var cts = new CancellationTokenSource();
            var loop = AgentCommandWiring.StartHeartbeatLoop(MakeSession(link), 60, cts.Token,
                statePath: statePath);

            while (AccessLiveness.LastSeen(statePath) is null) await Task.Delay(5);
            cts.Cancel();
            try { await loop; } catch (OperationCanceledException) { }

            Assert.NotNull(AccessLiveness.LastSeen(statePath));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Loop_StopsAndReports_WhenAccessWasRevertedOutside()
    {
        // Регрессия (п.81): после отката watchdog'ом агент продолжал слать heartbeat, и в CLI
        // висело «online · готов» при снятом доступе.
        var statePath = Path.Combine(Path.GetTempPath(), $"szgone-{Guid.NewGuid():N}.json");
        var link = new CountingLink(shouldThrow: false);
        var revoked = false;
        using var cts = new CancellationTokenSource();

        var loop = AgentCommandWiring.StartHeartbeatLoop(MakeSession(link), 60, cts.Token,
            statePath: statePath, onAccessRevoked: () => revoked = true);
        await loop;   // цикл выходит сам: файла состояния нет

        Assert.True(revoked);
        Assert.False(cts.IsCancellationRequested);   // выход по своей причине, а не по отмене
    }

    [Fact]
    public async Task NoCallback_StillWorks()
    {
        var link = new CountingLink(shouldThrow: false);
        using var cts = new CancellationTokenSource();
        var loop = AgentCommandWiring.StartHeartbeatLoop(MakeSession(link), 60, cts.Token);
        while (link.Calls == 0) await Task.Delay(5);
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
        Assert.True(link.Calls >= 1);
    }
}
