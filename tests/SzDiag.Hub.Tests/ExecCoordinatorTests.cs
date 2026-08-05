using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class ExecCoordinatorTests
{
    private sealed class SpySender : IAgentCommandSender
    {
        public List<ExecRequest> Sent { get; } = new();
        public Task SendRevertAsync(string c, string sz, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunTestsAsync(string c, string sz, string? f, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunDiagAsync(string c, string sz, string? s, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendExecAsync(string connectionId, ExecRequest request, CancellationToken ct = default)
        {
            Sent.Add(request);
            return Task.CompletedTask;
        }
        public Task SendPullAsync(string connectionId, PullRequest request, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendPushAsync(string c, PushRequest request, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static SessionRegistry RegistryWith(string sz)
    {
        var reg = new SessionRegistry();
        reg.Register(sz, "10.0.0.42", "PC-1", "conn-1");
        return reg;
    }

    [Fact]
    public async Task RunAsync_OfflineSz_ReturnsNull()
    {
        var coordinator = new ExecCoordinator(new SessionRegistry(), new SpySender());

        Assert.Null(await coordinator.RunAsync("160306", "Get-Date"));
    }

    [Fact]
    public async Task RunAsync_AgentAnswers_ReturnsResult()
    {
        var sender = new SpySender();
        var coordinator = new ExecCoordinator(RegistryWith("160306"), sender);

        var call = coordinator.RunAsync("160306", "Get-Date");
        // Ответ приходит отдельным вызовом от агента — сопоставляется по RequestId.
        var sent = await WaitForRequest(sender);
        coordinator.Complete(new ExecResult(sent.RequestId, 0, "28.07.2026", ""));

        var result = await call;
        Assert.Equal(0, result!.ExitCode);
        Assert.Equal("28.07.2026", result.StdOut);
    }

    [Fact]
    public async Task RunAsync_PassesScriptAndTimeoutToAgent()
    {
        var sender = new SpySender();
        var coordinator = new ExecCoordinator(RegistryWith("160306"), sender);

        var call = coordinator.RunAsync("160306", "whoami", timeoutSeconds: 45);
        var sent = await WaitForRequest(sender);
        coordinator.Complete(new ExecResult(sent.RequestId, 0, "svc-diag", ""));
        await call;

        Assert.Equal("whoami", sent.Script);
        Assert.Equal(45, sent.TimeoutSeconds);
        Assert.Equal("160306", sent.Sz);
    }

    [Fact]
    public async Task Complete_UnknownRequestId_Ignored()
    {
        var coordinator = new ExecCoordinator(RegistryWith("160306"), new SpySender());

        // Ответ на истёкший/чужой запрос не должен ронять hub.
        Assert.False(coordinator.Complete(new ExecResult("нет-такого", 0, "x", "")));
    }

    [Fact]
    public async Task RunAsync_AgentSilent_ThrowsTimeoutAndForgetsRequest()
    {
        // grace=0 и timeout=0 → ждать нечего, таймаут срабатывает сразу.
        var coordinator = new ExecCoordinator(RegistryWith("160306"), new SpySender(), graceSeconds: 0);

        var call = coordinator.RunAsync("160306", "Start-Sleep 999", timeoutSeconds: 0);

        await Assert.ThrowsAsync<TimeoutException>(() => call);
        Assert.Equal(0, coordinator.PendingCount); // запрос не должен «протекать»
    }

    private static async Task<ExecRequest> WaitForRequest(SpySender sender)
    {
        for (var i = 0; i < 100 && sender.Sent.Count == 0; i++) await Task.Delay(10);
        return Assert.Single(sender.Sent);
    }
}
