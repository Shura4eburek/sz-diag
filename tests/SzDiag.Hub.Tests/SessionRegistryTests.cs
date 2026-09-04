using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class SessionRegistryTests
{
    private static SessionRegistry NewRegistry() => new();

    [Fact]
    public void Register_AddsOnlineSession()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        var active = reg.GetActive();
        var s = Assert.Single(active);
        Assert.Equal("156864", s.Sz);
        Assert.Equal("10.0.0.42", s.Ip);
        Assert.Equal("PC-1", s.Hostname);
        Assert.Equal(SessionStatus.Online, s.Status);
    }

    // #194/бэклог п.220: hub обязан знать, под кем и в какой сессии живёт агент — GUI-операции
    // ломаются молча из session 0 (СЗ 123123).
    [Fact]
    public void Register_WithAgentIdentity_StoresUserAndSession()
    {
        var reg = NewRegistry();
        reg.Register("123123", "10.0.0.42", "PC-1", "conn-1",
            agentUser: @"NT AUTHORITY\СИСТЕМА", agentSessionId: 0);

        var s = Assert.Single(reg.GetActive());
        Assert.Equal(@"NT AUTHORITY\СИСТЕМА", s.AgentUser);
        Assert.Equal(0, s.AgentSessionId);
        Assert.True(s.AgentInSessionZero);
    }

    [Fact]
    public void Register_UserSession_IsNotSessionZero()
    {
        var reg = NewRegistry();
        reg.Register("123123", "10.0.0.42", "PC-1", "conn-1",
            agentUser: @"DESKTOP-1\kiril", agentSessionId: 1);

        Assert.False(Assert.Single(reg.GetActive()).AgentInSessionZero);
    }

    [Fact]
    public void Register_WithBootTime_StoresIt()
    {
        var reg = NewRegistry();
        var boot = new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.Zero);
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-1", boot);

        Assert.Equal(boot, Assert.Single(reg.GetActive()).BootTime);
    }

    [Fact]
    public void Register_SameBootTime_NotTreatedAsReboot()
    {
        // Переподключение агента (упал SignalR, сеть моргнула) — машина при этом не ребутилась.
        var reg = NewRegistry();
        var boot = new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.Zero);
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-1", boot);

        var rebooted = reg.Register("160306", "10.0.0.42", "PC-1", "conn-2", boot);

        Assert.False(rebooted.Rebooted);
        Assert.Null(Assert.Single(reg.GetActive()).LastRebootAt);
    }

    [Fact]
    public void Register_ChangedBootTime_DetectsReboot()
    {
        var reg = NewRegistry();
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-1",
            new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.Zero));

        var rebooted = reg.Register("160306", "10.0.0.42", "PC-1", "conn-2",
            new DateTimeOffset(2026, 7, 28, 13, 05, 0, TimeSpan.Zero));

        Assert.True(rebooted.Rebooted);
        Assert.NotNull(Assert.Single(reg.GetActive()).LastRebootAt);
    }

    [Fact]
    public void Register_BootTimeUnknown_NoFalseReboot()
    {
        // Агент старой сборки boot-time не шлёт: молчание не должно выглядеть как ребут.
        var reg = NewRegistry();
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-1",
            new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.Zero));

        var rebooted = reg.Register("160306", "10.0.0.42", "PC-1", "conn-2", bootTime: null);

        Assert.False(rebooted.Rebooted);
    }

    [Fact]
    public void Register_SameSzTwice_ReplacesConnection()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.Register("156864", "10.0.0.43", "PC-1", "conn-2");

        Assert.Single(reg.GetActive());
        Assert.Equal("conn-2", reg.TryGetConnectionId("156864"));
    }

    [Fact]
    public void Heartbeat_UpdatesLastHeartbeatAndSetsOnline()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.MarkOfflineByConnection("conn-1");

        var updated = reg.Heartbeat("156864");

        Assert.True(updated);
        Assert.Equal(SessionStatus.Online, reg.GetActive().Single().Status);
    }

    [Fact]
    public void Heartbeat_UnknownSz_ReturnsFalse()
    {
        var reg = NewRegistry();
        Assert.False(reg.Heartbeat("000000"));
    }

    [Fact]
    public void MarkOfflineByConnection_SetsStatusOffline()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        var sz = reg.MarkOfflineByConnection("conn-1");

        Assert.Equal("156864", sz);
        Assert.Equal(SessionStatus.Offline, reg.GetActive().Single().Status);
    }

    [Fact]
    public void Remove_DeletesSession()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");

        reg.Remove("156864");

        Assert.Empty(reg.GetActive());
        Assert.Null(reg.TryGetConnectionId("156864"));
    }

    [Fact]
    public void TryGetConnectionId_UnknownSz_ReturnsNull()
    {
        var reg = NewRegistry();
        Assert.Null(reg.TryGetConnectionId("000000"));
    }

    [Fact]
    public void SetActivity_UpdatesActivityAndSince()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var since = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

        var ok = reg.SetActivity("156864", "Тест OCCT", since);

        Assert.True(ok);
        var s = reg.GetActive().Single();
        Assert.Equal("Тест OCCT", s.Activity);
        Assert.Equal(since, s.ActivitySince);
    }

    [Fact]
    public void SetActivity_UnknownSz_ReturnsFalse()
        => Assert.False(NewRegistry().SetActivity("000000", "x", null));

    [Fact]
    public void Heartbeat_PreservesActivity()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.SetActivity("156864", "Тест OCCT", DateTimeOffset.UtcNow);

        reg.Heartbeat("156864");

        Assert.Equal("Тест OCCT", reg.GetActive().Single().Activity);
    }

    [Fact]
    public void Register_AfterFailedRevert_ClearsRevertNote()
    {
        // Important-8 (ревью волны 1): агент, переподнявшийся после неудачного watchdog-
        // отката (--resume), должен снова выглядеть просто "online", а не навсегда висеть
        // как «⚠ откат» — StatusCell проверяет RevertNote раньше Status == Online.
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.MarkRevertOutcome("156864", success: false, "sshd не снят: Access denied");
        Assert.NotNull(reg.GetActive().Single().RevertNote);

        reg.Register("156864", "10.0.0.42", "PC-1", "conn-2");

        var info = reg.GetActive().Single();
        Assert.Null(info.RevertNote);
        Assert.Equal(SessionStatus.Online, info.Status);
    }

    // Плановое обесточивание сервиса (бэклог п.130): пропажа heartbeat у нескольких СЗ разом
    // не должна засчитываться как дефект одной машины.
    [Fact]
    public void WasMassOfflineNear_NoEventYet_ReturnsFalse()
        => Assert.False(NewRegistry().WasMassOfflineNear(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30)));

    [Fact]
    public void WasMassOfflineNear_WithinWindow_ReturnsTrue()
    {
        var reg = NewRegistry();
        var at = new DateTimeOffset(2026, 8, 12, 6, 0, 0, TimeSpan.Zero);
        reg.RecordMassOfflineEvent(at);

        Assert.True(reg.WasMassOfflineNear(at + TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void WasMassOfflineNear_OutsideWindow_ReturnsFalse()
    {
        var reg = NewRegistry();
        var at = new DateTimeOffset(2026, 8, 12, 6, 0, 0, TimeSpan.Zero);
        reg.RecordMassOfflineEvent(at);

        Assert.False(reg.WasMassOfflineNear(at + TimeSpan.FromHours(2), TimeSpan.FromMinutes(30)));
    }

    // Переподключение после пропажи heartbeat БЕЗ смены boot-time — тоже факт диагностики
    // (бэклог п.202, СЗ 161972): «вырубился или висит» иначе выясняется только руками.
    [Fact]
    public void Register_ReconnectAfterOfflineGap_ReportsGapAndActivity()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 21, 16, 30, 0, TimeSpan.Zero));
        var reg = new SessionRegistry(time);
        var boot = new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);
        reg.Register("161972", "10.0.0.5", "PC-1", "conn-1", boot);
        reg.SetActivity("161972", "Disk linear scan", DateTimeOffset.UtcNow);

        time.Advance(TimeSpan.FromMinutes(1));
        reg.MarkStaleOffline(TimeSpan.FromSeconds(1));   // heartbeat пропал

        time.Advance(TimeSpan.FromMinutes(5));
        var outcome = reg.Register("161972", "10.0.0.5", "PC-1", "conn-2", boot); // тот же boot-time

        Assert.False(outcome.Rebooted);
        Assert.NotNull(outcome.ReconnectedAfterGap);
        // Молчание считается от ПОСЛЕДНЕГО heartbeat (16:30), а не от момента offline-пометки:
        // 1 минута до пометки + 5 минут после = 6.
        Assert.Equal(TimeSpan.FromMinutes(6), outcome.ReconnectedAfterGap!.Value);
        Assert.Equal("Disk linear scan", outcome.ActivityBefore);
    }

    [Fact]
    public void Register_ReconnectWithoutPriorOfflineMark_NoGapReported()
    {
        // Обычное переподключение сразу после разрыва SignalR (не через offline-sweep) —
        // не должно печатать «мовчала N хв» на пустом месте.
        var reg = NewRegistry();
        var boot = new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);
        reg.Register("161972", "10.0.0.5", "PC-1", "conn-1", boot);

        var outcome = reg.Register("161972", "10.0.0.5", "PC-1", "conn-2", boot);

        Assert.Null(outcome.ReconnectedAfterGap);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
