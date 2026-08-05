using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class TestRunTriggerTests
{
    private sealed class SpySender : IAgentCommandSender
    {
        public List<(string conn, string sz)> Reverts { get; } = new();
        public List<(string conn, string sz, string? filter)> Tests { get; } = new();
        public Task SendRevertAsync(string c, string sz, CancellationToken ct = default) { Reverts.Add((c, sz)); return Task.CompletedTask; }
        public Task SendRunTestsAsync(string c, string sz, string? filter, CancellationToken ct = default) { Tests.Add((c, sz, filter)); return Task.CompletedTask; }
        public List<(string conn, string sz, string? sections)> Diags { get; } = new();
        public Task SendRunDiagAsync(string c, string sz, string? sections, CancellationToken ct = default) { Diags.Add((c, sz, sections)); return Task.CompletedTask; }
        public Task SendExecAsync(string connectionId, SzDiag.Contracts.ExecRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPullAsync(string connectionId, SzDiag.Contracts.PullRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPushAsync(string connectionId, SzDiag.Contracts.PushRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Trigger_KnownSz_PushesRunTests()
    {
        var reg = new SessionRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var sender = new SpySender();
        var trigger = new TestRunTrigger(reg, sender);

        var ok = await trigger.TriggerAsync("156864");

        Assert.True(ok);
        Assert.Equal(("conn-1", "156864", (string?)null), sender.Tests.Single());
    }

    [Fact]
    public async Task Trigger_WithFilter_PassesFilterThrough()
    {
        var reg = new SessionRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var sender = new SpySender();
        var trigger = new TestRunTrigger(reg, sender);

        await trigger.TriggerAsync("156864", "occt");

        Assert.Equal("occt", sender.Tests.Single().filter);
    }

    [Fact]
    public async Task Trigger_UnknownSz_ReturnsFalse()
    {
        var trigger = new TestRunTrigger(new SessionRegistry(), new SpySender());
        Assert.False(await trigger.TriggerAsync("000000"));
    }
}
