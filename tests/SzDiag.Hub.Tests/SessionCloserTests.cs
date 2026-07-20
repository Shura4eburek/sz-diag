using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class SessionCloserTests
{
    private sealed class SpyCommandSender : IAgentCommandSender
    {
        public List<(string conn, string sz)> Sent { get; } = new();
        public Task SendRevertAsync(string connectionId, string sz, CancellationToken ct = default)
        {
            Sent.Add((connectionId, sz));
            return Task.CompletedTask;
        }
        public Task SendRunTestsAsync(string connectionId, string sz, string? filter, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunDiagAsync(string connectionId, string sz, string? sections, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class SpyStore : ISessionStore
    {
        public List<string> Closed { get; } = new();
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordOpenAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordCloseAsync(string sz, DateTimeOffset closedAt, CancellationToken ct = default)
        {
            Closed.Add(sz);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SessionRecord>> GetHistoryAsync(CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<SessionRecord>)new List<SessionRecord>());
    }

    [Fact]
    public async Task Close_KnownOnlineSz_SendsRevertRecordsCloseAndRemoves()
    {
        var reg = new SessionRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var sender = new SpyCommandSender();
        var store = new SpyStore();
        var closer = new SessionCloser(reg, store, sender);

        var ok = await closer.CloseAsync("156864");

        Assert.True(ok);
        Assert.Equal(("conn-1", "156864"), sender.Sent.Single());
        Assert.Equal("156864", store.Closed.Single());
        Assert.Empty(reg.GetActive());
    }

    [Fact]
    public async Task Close_UnknownSz_ReturnsFalseAndDoesNothing()
    {
        var reg = new SessionRegistry();
        var sender = new SpyCommandSender();
        var store = new SpyStore();
        var closer = new SessionCloser(reg, store, sender);

        var ok = await closer.CloseAsync("000000");

        Assert.False(ok);
        Assert.Empty(sender.Sent);
        Assert.Empty(store.Closed);
    }

    [Fact]
    public async Task Close_OfflineSz_RecordsCloseAndRemoves()
    {
        var reg = new SessionRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.MarkOfflineByConnection("conn-1"); // сессия офлайн, но connectionId ещё в реестре
        var sender = new SpyCommandSender();
        var store = new SpyStore();
        var closer = new SessionCloser(reg, store, sender);

        var ok = await closer.CloseAsync("156864");

        Assert.True(ok);
        Assert.Equal("156864", store.Closed.Single());
        Assert.Empty(reg.GetActive());
    }
}
