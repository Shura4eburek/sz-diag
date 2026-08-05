using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class DiagRunTriggerTests
{
    private sealed class SpySender : IAgentCommandSender
    {
        public List<(string conn, string sz, string? sections)> Diags { get; } = new();
        public Task SendRevertAsync(string c, string sz, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunTestsAsync(string c, string sz, string? filter, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunDiagAsync(string c, string sz, string? sections, CancellationToken ct = default)
        { Diags.Add((c, sz, sections)); return Task.CompletedTask; }
        public Task SendExecAsync(string connectionId, SzDiag.Contracts.ExecRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendExecStatusAsync(string connectionId, SzDiag.Contracts.ExecStatusRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPullAsync(string connectionId, SzDiag.Contracts.PullRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPushAsync(string connectionId, SzDiag.Contracts.PushRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Trigger_KnownSz_PushesRunDiag()
    {
        var reg = new SessionRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var sender = new SpySender();

        var ok = await new DiagRunTrigger(reg, sender).TriggerAsync("156864", "storage,events");

        Assert.True(ok);
        Assert.Equal(("conn-1", "156864", (string?)"storage,events"), sender.Diags.Single());
    }

    [Fact]
    public async Task Trigger_UnknownSz_ReturnsFalse()
    {
        var sender = new SpySender();
        Assert.False(await new DiagRunTrigger(new SessionRegistry(), sender).TriggerAsync("000000"));
        Assert.Empty(sender.Diags);
    }
}
