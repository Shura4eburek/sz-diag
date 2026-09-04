using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Перезапуск агента — отдельный от exec SignalR-путь (бэклог п.202/п.215):
/// раньше `agent restart` сам ходил через exec-очередь и был бесполезен ровно тогда, когда
/// нужен (канал забит). Эти тесты проверяют, что триггер вообще не трогает ExecCoordinator —
/// команда идёт напрямую через IAgentCommandSender.</summary>
public class RestartAgentTriggerTests
{
    private sealed class SpySender : IAgentCommandSender
    {
        public List<string> RestartedSz { get; } = new();
        public Task SendRevertAsync(string c, string sz, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunTestsAsync(string c, string sz, string? f, string? schedule = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunDiagAsync(string c, string sz, string? s, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendExecAsync(string c, ExecRequest r, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendExecStatusAsync(string c, ExecStatusRequest r, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPullAsync(string c, PullRequest r, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPushAsync(string c, PushRequest r, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRestartAgentAsync(string c, string sz, CancellationToken ct = default)
        {
            RestartedSz.Add(sz);
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
    public async Task TriggerAsync_OnlineSz_SendsRestartAndReturnsTrue()
    {
        var sender = new SpySender();
        var trigger = new RestartAgentTrigger(RegistryWith("160705"), sender);

        var ok = await trigger.TriggerAsync("160705");

        Assert.True(ok);
        Assert.Contains("160705", sender.RestartedSz);
    }

    [Fact]
    public async Task TriggerAsync_OfflineSz_ReturnsFalseWithoutSending()
    {
        var sender = new SpySender();
        var trigger = new RestartAgentTrigger(new SessionRegistry(), sender);

        var ok = await trigger.TriggerAsync("999999");

        Assert.False(ok);
        Assert.Empty(sender.RestartedSz);
    }
}
