using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Ack приёма команды и опрос фоновой задачи: «агент не принял команду» и
/// «принял, но задавлен нагрузкой» — разные диагнозы, а раньше выглядели одинаково
/// (бэклог п.35/п.43).</summary>
public class ExecAckAndJobTests
{
    private sealed class SpySender : IAgentCommandSender
    {
        public List<ExecRequest> Sent { get; } = new();
        public List<ExecStatusRequest> StatusSent { get; } = new();
        public Func<ExecRequest, Task>? OnExec { get; set; }
        public Func<ExecStatusRequest, Task>? OnStatus { get; set; }

        public Task SendRevertAsync(string c, string sz, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunTestsAsync(string c, string sz, string? f, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunDiagAsync(string c, string sz, string? s, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPullAsync(string c, PullRequest r, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPushAsync(string c, PushRequest r, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendExecAsync(string c, ExecRequest request, CancellationToken ct = default)
        {
            Sent.Add(request);
            if (OnExec is not null) _ = Task.Run(() => OnExec(request));
            return Task.CompletedTask;
        }

        public Task SendExecStatusAsync(string c, ExecStatusRequest request, CancellationToken ct = default)
        {
            StatusSent.Add(request);
            if (OnStatus is not null) _ = Task.Run(() => OnStatus(request));
            return Task.CompletedTask;
        }
    }

    private static SessionRegistry RegistryWith(string sz)
    {
        var reg = new SessionRegistry();
        reg.Register(sz, "10.0.0.42", "PC-1", "conn-1");
        return reg;
    }

    [Fact]
    public async Task Timeout_WithoutAck_SaysCommandWasNotAccepted()
    {
        var coordinator = new ExecCoordinator(RegistryWith("160636"), new SpySender(), graceSeconds: 0);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => coordinator.RunAsync("160636", "'ok'", timeoutSeconds: 1));

        Assert.Contains("не принял команду", ex.Message);
    }

    [Fact]
    public async Task Timeout_AfterAck_BlamesLoadAndSuggestsDetach()
    {
        // Агент ответил ack, но результата нет: это «задавлен нагрузкой», а не «связь пропала».
        var sender = new SpySender();
        var coordinator = new ExecCoordinator(RegistryWith("160636"), sender, graceSeconds: 0);
        sender.OnExec = req =>
        {
            coordinator.Acknowledge(new ExecAck(req.RequestId, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        };

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => coordinator.RunAsync("160636", "Start-Sleep 999", timeoutSeconds: 1));

        Assert.Contains("ПРИНЯЛ команду", ex.Message);
        Assert.Contains("--detach", ex.Message);
    }

    [Fact]
    public async Task Detached_FlagReachesAgent()
    {
        var sender = new SpySender();
        var coordinator = new ExecCoordinator(RegistryWith("160636"), sender, graceSeconds: 0);
        sender.OnExec = req =>
        {
            coordinator.Complete(new ExecResult(req.RequestId, 0, "запущено", "", JobId: "job-1"));
            return Task.CompletedTask;
        };

        var result = await coordinator.RunAsync("160636", "долгий скрипт", 30, detached: true);

        Assert.True(Assert.Single(sender.Sent).Detached);
        Assert.Equal("job-1", result!.JobId);
    }

    [Fact]
    public async Task Status_ReturnsAgentAnswer()
    {
        var sender = new SpySender();
        var coordinator = new ExecCoordinator(RegistryWith("160636"), sender, graceSeconds: 0);
        sender.OnStatus = req =>
        {
            coordinator.CompleteStatus(new ExecJobStatus(req.RequestId, req.JobId, true, null,
                "tick 42", DateTimeOffset.UtcNow.AddMinutes(-5), 1024));
            return Task.CompletedTask;
        };

        var status = await coordinator.StatusAsync("160636", "job-1", tailLines: 10);

        Assert.True(status!.Running);
        Assert.Contains("tick 42", status.Tail);
        Assert.Equal(10, Assert.Single(sender.StatusSent).TailLines);
    }

    [Fact]
    public async Task Status_OfflineSz_ReturnsNull()
    {
        var coordinator = new ExecCoordinator(new SessionRegistry(), new SpySender(), graceSeconds: 0);

        Assert.Null(await coordinator.StatusAsync("999999", "job-1", 10));
    }

    [Fact]
    public void Acknowledge_UnknownRequestId_Ignored()
    {
        var coordinator = new ExecCoordinator(RegistryWith("160636"), new SpySender());

        Assert.False(coordinator.Acknowledge(new ExecAck("чужой-id", DateTimeOffset.UtcNow)));
    }
}
