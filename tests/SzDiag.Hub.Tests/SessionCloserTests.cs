using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class SessionCloserTests
{
    private sealed class SpyCommandSender : IAgentCommandSender
    {
        public List<(string conn, string sz)> Sent { get; } = new();

        /// <summary>Имитирует агента, который отвечает итогом отката сразу по получении
        /// Revert — как это реально устроено (см. AgentSession.DoRevertAsync).</summary>
        public Action? OnSendRevert { get; set; }

        public Task SendRevertAsync(string connectionId, string sz, CancellationToken ct = default)
        {
            Sent.Add((connectionId, sz));
            OnSendRevert?.Invoke();
            return Task.CompletedTask;
        }
        public Task SendRunTestsAsync(string connectionId, string sz, string? filter, string? schedule = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunDiagAsync(string connectionId, string sz, string? sections, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendExecAsync(string connectionId, SzDiag.Contracts.ExecRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendExecStatusAsync(string connectionId, SzDiag.Contracts.ExecStatusRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPullAsync(string connectionId, SzDiag.Contracts.PullRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPushAsync(string connectionId, SzDiag.Contracts.PushRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRestartAgentAsync(string connectionId, string sz, CancellationToken ct = default) => Task.CompletedTask;
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
        public Task AddMaintenanceAsync(MaintenanceWindow window, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MaintenanceWindow>> GetMaintenanceAsync(string sz, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MaintenanceWindow>>(Array.Empty<MaintenanceWindow>());
        public Task<IReadOnlyList<PowerEvent>> MergeJournalEventsAsync(PowerEventsReport report, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PowerEvent>>(Array.Empty<PowerEvent>());
        public Task SetLastTestConfigAsync(string sz, string config, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<string?> GetLastTestConfigAsync(string sz, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task RecordRebootAsync(SzDiag.Contracts.RebootEvent evt, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<SzDiag.Contracts.RebootTimeline> GetRebootsAsync(string sz, CancellationToken ct = default)
            => Task.FromResult(new SzDiag.Contracts.RebootTimeline(sz, Array.Empty<SzDiag.Contracts.RebootEvent>(), null));
    }

    [Fact]
    public async Task Close_KnownOnlineSz_SendsRevertRecordsCloseAndRemoves()
    {
        var reg = new SessionRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var sender = new SpyCommandSender();
        var store = new SpyStore();
        var closer = new SessionCloser(reg, store, sender, new RevertResultStore(), TimeSpan.FromMilliseconds(50));

        var outcome = await closer.CloseAsync("156864");

        Assert.True(outcome.Closed);
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
        var closer = new SessionCloser(reg, store, sender, new RevertResultStore());

        var outcome = await closer.CloseAsync("000000");

        Assert.False(outcome.Closed);
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
        var closer = new SessionCloser(reg, store, sender, new RevertResultStore());

        var outcome = await closer.CloseAsync("156864");

        Assert.True(outcome.Closed);
        Assert.Null(outcome.Revert);   // офлайн-агент уже не ответит — ждать нечего (п.119)
        Assert.Equal("156864", store.Closed.Single());
        Assert.Empty(reg.GetActive());
    }

    [Fact]
    public async Task Close_OnlineSz_PicksUpRevertResultReportedByAgent()
    {
        // Регрессия (бэклог п.119): `close` должен подхватить итог отката, если агент успел
        // прислать его до отключения канала — иначе полноту отката подтвердить нечем.
        var reg = new SessionRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var store = new SpyStore();
        var revertResults = new RevertResultStore();
        var sender = new SpyCommandSender
        {
            OnSendRevert = () => revertResults.Set(
                new RevertResult("156864", new[] { "sshd" }, Array.Empty<RevertResultFailure>()))
        };
        var closer = new SessionCloser(reg, store, sender, revertResults, TimeSpan.FromSeconds(1));

        var outcome = await closer.CloseAsync("156864");

        Assert.True(outcome.Closed);
        Assert.NotNull(outcome.Revert);
        Assert.True(outcome.Revert!.AllClean);
    }

    [Fact]
    public async Task Close_OfflineSz_PicksUpFreshRevertResultReceivedBeforeDisconnect()
    {
        // Critical-2 (ревью волны 1): агент откатился сам (клавиша C), успел прислать сводку,
        // и ТОЛЬКО ПОТОМ канал упал — к моменту close сессия уже offline. Раньше здесь стоял
        // безусловный Remove(sz) ДО отправки revert, и сводка терялась ровно в этом сценарии
        // (#55 делался именно ради него).
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var reg = new SessionRegistry(time);
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var revertResults = new RevertResultStore(time);
        time.Advance(TimeSpan.FromSeconds(5));   // self-revert случился уже во время сессии
        revertResults.Set(new RevertResult("156864", new[] { "sshd" },
            new[] { new RevertResultFailure("firewall", "Access denied") }));
        reg.MarkOfflineByConnection("conn-1");
        var sender = new SpyCommandSender();
        var store = new SpyStore();
        var closer = new SessionCloser(reg, store, sender, revertResults);

        var outcome = await closer.CloseAsync("156864");

        Assert.True(outcome.Closed);
        Assert.NotNull(outcome.Revert);
        Assert.False(outcome.Revert!.AllClean);
        Assert.Equal("156864", store.Closed.Single());
    }

    [Fact]
    public async Task Close_OfflineSz_DiscardsRevertResultFromPreviousSession()
    {
        // Сводка от предыдущей (уже закрытой и переоткрытой) сессии этой же СЗ не должна
        // выдаваться за итог текущего отката.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var revertResults = new RevertResultStore(time);
        revertResults.Set(new RevertResult("156864", new[] { "sshd" }, Array.Empty<RevertResultFailure>()));
        time.Advance(TimeSpan.FromMinutes(10));   // СЗ переоткрылась новой сессией
        var reg = new SessionRegistry(time);
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.MarkOfflineByConnection("conn-1");
        var sender = new SpyCommandSender();
        var store = new SpyStore();
        var closer = new SessionCloser(reg, store, sender, revertResults);

        var outcome = await closer.CloseAsync("156864");

        Assert.True(outcome.Closed);
        Assert.Null(outcome.Revert);
    }

    // review W2 I-4: "свежая" проверка (TryGetFresh) стояла только для pending — цикл ожидания
    // ДАЛЬШЕ звал TryGet(sz) без учёта ConnectedAt, и первая же итерация подхватывала ту же
    // устаревшую сводку заново. Онлайн-сессия (в отличие от DiscardsRevertResultFromPreviousSession
    // выше) — единственный сценарий, где цикл вообще выполняется.
    [Fact]
    public async Task Close_OnlineSz_LoopDoesNotPickUpStaleRevertResult()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));
        var revertResults = new RevertResultStore(time);
        revertResults.Set(new RevertResult("156864", new[] { "sshd" }, Array.Empty<RevertResultFailure>()));
        time.Advance(TimeSpan.FromMinutes(10)); // СЗ переоткрылась новой сессией
        var reg = new SessionRegistry(time);
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1"); // онлайн, ConnectedAt позже старой сводки
        var sender = new SpyCommandSender(); // агент молчит — новой сводки не будет
        var store = new SpyStore();
        var closer = new SessionCloser(reg, store, sender, revertResults, revertWait: TimeSpan.FromMilliseconds(50));

        var outcome = await closer.CloseAsync("156864");

        Assert.True(outcome.Closed);
        Assert.Null(outcome.Revert); // старая сводка не должна выдаваться за итог текущего отката
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
