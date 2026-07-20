using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class AgentSessionTests
{
    private sealed class FakeManager : ISystemAccessManager
    {
        public int OpenCalls { get; private set; }
        public int RevertCalls { get; private set; }
        public RevertState Open(AccessSpec spec)
        {
            OpenCalls++;
            return new RevertState { Sz = spec.Sz };
        }
        public void Revert(RevertState state) => RevertCalls++;
    }

    private sealed class FakeHubLink : IHubLink
    {
        public bool Connected { get; private set; }
        public string? RegisteredSz { get; private set; }
        public int Heartbeats { get; private set; }
        public bool Disposed { get; private set; }
        private Func<string, Task>? _onRevert;

        public Task ConnectAsync(CancellationToken ct = default) { Connected = true; return Task.CompletedTask; }
        public Task RegisterAsync(string sz, string hostname, CancellationToken ct = default) { RegisteredSz = sz; return Task.CompletedTask; }
        public Task HeartbeatAsync(string sz, CancellationToken ct = default) { Heartbeats++; return Task.CompletedTask; }
        public void OnRevert(Func<string, Task> handler) => _onRevert = handler;
        public List<SzDiag.Contracts.UploadReportPart> Uploaded { get; } = new();
        private Func<string, string?, Task>? _onRunTests;
        public void OnRunTests(Func<string, string?, Task> handler) => _onRunTests = handler;
        public void OnRunDiag(Func<string, string?, Task> handler) { }
        public Task UploadReportFileAsync(SzDiag.Contracts.UploadReportPart part, CancellationToken ct = default)
        {
            Uploaded.Add(part);
            return Task.CompletedTask;
        }
        public Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default)
            => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }

        public Task FireRevert(string sz) => _onRevert!(sz);
        public Task FireRunTests(string sz, string? filter = null) => _onRunTests!(sz, filter);
    }

    private static AccessSpec Spec() =>
        new("156864", "svc-diag", "ssh-ed25519 AAAA...", 22, TimeSpan.FromHours(6));

    [Fact]
    public async Task StartAsync_OpensAccessConnectsAndRegisters()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");

        await session.StartAsync();

        Assert.Equal(1, mgr.OpenCalls);
        Assert.True(link.Connected);
        Assert.Equal("156864", link.RegisteredSz);
    }

    [Fact]
    public async Task HeartbeatOnceAsync_SendsHeartbeat()
    {
        var link = new FakeHubLink();
        var session = new AgentSession(new FakeManager(), link, Spec(), "PC-1");
        await session.StartAsync();

        await session.HeartbeatOnceAsync();

        Assert.Equal(1, link.Heartbeats);
    }

    [Fact]
    public async Task RevertFromHub_RevertsOnceAndDisposesLink()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");
        await session.StartAsync();

        await link.FireRevert("156864");
        await link.FireRevert("156864"); // повторный триггер

        Assert.Equal(1, mgr.RevertCalls);
        Assert.True(link.Disposed);
    }

    [Fact]
    public async Task RevertLocalAsync_RevertsOnce()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");
        await session.StartAsync();

        await session.RevertAsync();
        await session.RevertAsync();

        Assert.Equal(1, mgr.RevertCalls);
    }
}
