using Microsoft.Data.Sqlite;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Журнал вырубонов: реестр отдаёт подробности смены boot-time, SQLite их хранит
/// и переживает рестарт hub (бэклог п.55/п.42).</summary>
public class RebootJournalTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szreboots-{Guid.NewGuid():N}.db");

    private string Conn => $"Data Source={_dbPath}";

    private static readonly DateTimeOffset Boot1 = new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Boot2 = new(2026, 7, 30, 16, 15, 5, TimeSpan.Zero);

    [Fact]
    public void Register_Reboot_ReportsUptimeAndActivity()
    {
        // Именно эти два поля и превращают «отвалилась» в «продержалась N часов под тестом».
        var reg = new SessionRegistry();
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-1", Boot1);
        reg.SetActivity("160306", "OCCT Combined", DateTimeOffset.UtcNow);

        var outcome = reg.Register("160306", "10.0.0.42", "PC-1", "conn-2", Boot2);

        Assert.True(outcome.Rebooted);
        Assert.Equal(Boot1, outcome.PreviousBootTime);
        Assert.Equal(Boot2 - Boot1, outcome.UptimeBefore);
        Assert.Equal("OCCT Combined", outcome.ActivityBefore);
    }

    [Fact]
    public void Register_Reboot_IncrementsCounterVisibleInList()
    {
        var reg = new SessionRegistry();
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-1", Boot1);
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-2", Boot2);
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-3", Boot2.AddHours(3));

        Assert.Equal(2, Assert.Single(reg.GetActive()).RebootCount);
    }

    [Fact]
    public void Register_PowerButtonShutdown_DoesNotCountAsFailure()
    {
        // Регрессия (п.93): выключение кнопкой меняет boot-time так же, как обрыв питания, и
        // раньше попадало в счётчик ⚡ наравне с дефектом — «5 вырубонов» вместо трёх.
        var reg = new SessionRegistry();
        reg.Register("161312", "10.0.0.42", "PC-1", "conn-1", Boot1, ShutdownKind.HardOff);

        var outcome = reg.Register("161312", "10.0.0.42", "PC-1", "conn-2", Boot2, ShutdownKind.PowerButton);

        Assert.True(outcome.Rebooted);                                    // ребут был
        Assert.Equal(0, Assert.Single(reg.GetActive()).RebootCount);      // но это не отказ
    }

    [Fact]
    public void Register_HardOff_CountsAsFailure()
    {
        var reg = new SessionRegistry();
        reg.Register("161312", "10.0.0.42", "PC-1", "conn-1", Boot1);
        reg.Register("161312", "10.0.0.42", "PC-1", "conn-2", Boot2, ShutdownKind.HardOff);

        Assert.Equal(1, Assert.Single(reg.GetActive()).RebootCount);
    }

    [Fact]
    public async Task Store_KeepsShutdownKind()
    {
        var store = new SqliteSessionStore(Conn);
        await store.InitializeAsync();

        await store.RecordRebootAsync(new RebootEvent("161312", Boot2, Boot1, Boot2, 100, null,
            ShutdownKind.PowerButton));
        await store.RecordRebootAsync(new RebootEvent("161312", Boot2.AddHours(1), Boot2,
            Boot2.AddHours(1), 3600, null, ShutdownKind.HardOff));

        var timeline = await store.GetRebootsAsync("161312");

        Assert.Equal(2, timeline.Count);
        Assert.Equal(ShutdownKind.PowerButton, timeline.Events[0].Kind);
        Assert.False(timeline.Events[0].IsFailure);
        Assert.True(timeline.Events[1].IsFailure);
    }

    [Fact]
    public void Register_BootTimeJitter_IsNotAReboot()
    {
        // Агент считает boot-time как «сейчас минус аптайм» (п.90), поэтому между запусками
        // значение гуляет на секунды — ребутом это быть не должно.
        var reg = new SessionRegistry();
        reg.Register("160467", "10.0.0.42", "PC-1", "conn-1", Boot1);

        var outcome = reg.Register("160467", "10.0.0.42", "PC-1", "conn-2", Boot1.AddSeconds(7));

        Assert.False(outcome.Rebooted);
        Assert.Equal(0, Assert.Single(reg.GetActive()).RebootCount);
    }

    [Fact]
    public void Register_BootTimeFromFuture_IsIgnored()
    {
        // WinPE стартует с дефолтной таймзоной и отдаёт boot-time на 11 часов вперёд: по нему
        // нельзя ни считать аптайм, ни заводить вырубоны (бэклог п.90).
        var reg = new SessionRegistry();

        reg.Register("159948", "10.0.0.42", "PE-1", "conn-1", DateTimeOffset.UtcNow.AddHours(11));

        Assert.Null(Assert.Single(reg.GetActive()).BootTime);
    }

    [Fact]
    public void Register_Reconnect_DoesNotInventReboot()
    {
        // Под нагрузкой heartbeat опаздывает и SignalR переподключается — это не вырубон.
        var reg = new SessionRegistry();
        reg.Register("160306", "10.0.0.42", "PC-1", "conn-1", Boot1);

        var outcome = reg.Register("160306", "10.0.0.42", "PC-1", "conn-2", Boot1);

        Assert.False(outcome.Rebooted);
        Assert.Equal(0, Assert.Single(reg.GetActive()).RebootCount);
    }

    [Fact]
    public async Task Store_RecordsTimelineWithMaxUptime()
    {
        var store = new SqliteSessionStore(Conn);
        await store.InitializeAsync();

        await store.RecordRebootAsync(new RebootEvent("160306", Boot2,
            Boot1, Boot2, (long)(Boot2 - Boot1).TotalSeconds, "OCCT Combined"));
        await store.RecordRebootAsync(new RebootEvent("160306", Boot2.AddHours(2),
            Boot2, Boot2.AddHours(2), 7200, null));
        await store.RecordRebootAsync(new RebootEvent("999999", Boot2, null, Boot2, 60, null));

        var timeline = await store.GetRebootsAsync("160306");

        Assert.Equal(2, timeline.Count);                       // чужая СЗ не подмешалась
        Assert.Equal(Boot2, timeline.Events[0].At);            // порядок — от старых к новым
        Assert.Equal("OCCT Combined", timeline.Events[0].ActivityBefore);
        Assert.Equal((Boot2 - Boot1), timeline.MaxUptime);     // 53 часа на стенде, как в п.55
    }

    [Fact]
    public async Task Store_SurvivesRestart()
    {
        // In-memory реестр рестарт hub не переживает — именно поэтому журнал в SQLite.
        var first = new SqliteSessionStore(Conn);
        await first.InitializeAsync();
        await first.RecordRebootAsync(new RebootEvent("160306", Boot2, Boot1, Boot2, 190_000, null));

        var second = new SqliteSessionStore(Conn);
        await second.InitializeAsync();   // повторная инициализация не должна ронять таблицу

        Assert.Equal(1, (await second.GetRebootsAsync("160306")).Count);
    }

    [Fact]
    public async Task Store_NoReboots_EmptyTimelineNotNull()
    {
        var store = new SqliteSessionStore(Conn);
        await store.InitializeAsync();

        var timeline = await store.GetRebootsAsync("160306");

        Assert.Equal(0, timeline.Count);
        Assert.Null(timeline.MaxUptime);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}
