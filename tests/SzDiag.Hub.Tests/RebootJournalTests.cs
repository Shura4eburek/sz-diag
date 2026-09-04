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
    public async Task Store_MergesJournalEvents_WithoutDuplicatingWhatHubSawItself()
    {
        // Регрессия (п.97): всё, что случилось до подключения агента, в таймлайн не попадало,
        // и «вырубонов не зафиксировано» читалось как «их не было».
        var store = new SqliteSessionStore(Conn);
        await store.InitializeAsync();
        await store.RecordOpenAsync(new SessionRecord("160636", "10.0.0.1", "PC-1", Boot1, null));
        await store.RecordRebootAsync(new RebootEvent("160636", Boot2, Boot1, Boot2, 100, null,
            ShutdownKind.HardOff));

        var added = await store.MergeJournalEventsAsync(new PowerEventsReport("160636", new[]
        {
            new PowerEvent(Boot2.AddSeconds(60), ShutdownKind.HardOff),   // то же событие ±5 мин
            new PowerEvent(Boot1.AddDays(-3), ShutdownKind.HardOff),      // до наблюдения — новое
            new PowerEvent(Boot1.AddDays(-2), ShutdownKind.PowerButton),  // кнопка, не дефект
        }));

        var timeline = await store.GetRebootsAsync("160636");

        Assert.Equal(2, added.Count);
        Assert.Equal(3, timeline.Count);
        Assert.Equal(2, timeline.Events.Count(e => e.Source == RebootSource.Journal));
        Assert.Equal(Boot1, timeline.WatchingSince);   // с какого момента вообще смотрели
        // Кнопка из журнала в отказы не идёт (п.93).
        Assert.Equal(2, timeline.Events.Count(e => e.IsFailure));
    }

    [Fact]
    public async Task Store_MergeKeepsBurstsOfJournalEvents()
    {
        // Регрессия (бэклог п.106): дедуп ±5 минут шёл по ВСЕЙ таблице, включая только что
        // вставленные события того же журнала — серия вырубонов каждые 2 минуты (самый
        // показательный симптом: перегрев/БЖ/питание) схлопывалась до одной записи.
        var store = new SqliteSessionStore(Conn);
        await store.InitializeAsync();

        var added = await store.MergeJournalEventsAsync(new PowerEventsReport("260306", new[]
        {
            new PowerEvent(Boot1, ShutdownKind.HardOff),
            new PowerEvent(Boot1.AddMinutes(2), ShutdownKind.HardOff),
            new PowerEvent(Boot1.AddMinutes(4), ShutdownKind.HardOff),
            new PowerEvent(Boot1.AddMinutes(6), ShutdownKind.HardOff),
        }));

        Assert.Equal(4, added.Count);
        Assert.Equal(4, (await store.GetRebootsAsync("260306")).Count);
    }

    [Fact]
    public async Task Store_MergeKeepsBugcheckCode_AndTimelineReturnsIt()
    {
        // Бэклог п.121: `szcli reboots` печатал «BSOD ×13» без кодов — код обязан доехать
        // от журнала клиента до таймлайна.
        var store = new SqliteSessionStore(Conn);
        await store.InitializeAsync();

        await store.MergeJournalEventsAsync(new PowerEventsReport("161346", new[]
        {
            new PowerEvent(Boot1, ShutdownKind.Bsod, Bugcheck: 239),   // 0xEF CRITICAL_PROCESS_DIED
            new PowerEvent(Boot1.AddHours(1), ShutdownKind.HardOff),
        }));

        var timeline = await store.GetRebootsAsync("161346");

        Assert.Equal(239, timeline.Events.Single(e => e.Kind == ShutdownKind.Bsod).Bugcheck);
        Assert.Null(timeline.Events.Single(e => e.Kind == ShutdownKind.HardOff).Bugcheck);
    }

    [Fact]
    public async Task Store_MergeSleepEvents_PersistsDurationAndFeedsTotalSleep()
    {
        // Бэклог п.140/222 (СЗ 161346): сутки «наблюдения» оказались 7 часами реальной
        // работы — без длительности сна наработка опиралась на голый аптайм.
        var store = new SqliteSessionStore(Conn);
        await store.InitializeAsync();

        var added = await store.MergeJournalEventsAsync(new PowerEventsReport("161346", new[]
        {
            new PowerEvent(new DateTimeOffset(2026, 8, 10, 19, 32, 0, TimeSpan.Zero),
                ShutdownKind.Sleep, DurationSeconds: 59400),   // 16,5 ч
            new PowerEvent(new DateTimeOffset(2026, 8, 11, 19, 48, 0, TimeSpan.Zero),
                ShutdownKind.Sleep, DurationSeconds: 61920),   // 17,2 ч
        }));

        Assert.Equal(2, added.Count);

        var timeline = await store.GetRebootsAsync("161346");
        Assert.All(timeline.Events, e => Assert.False(e.IsFailure));   // сон не идёт в счётчик ⚡
        Assert.Equal(TimeSpan.FromSeconds(59400 + 61920), timeline.TotalSleep);
    }

    [Fact]
    public async Task Store_MergeSameJournalTwice_IsIdempotent()
    {
        var store = new SqliteSessionStore(Conn);
        await store.InitializeAsync();
        var report = new PowerEventsReport("260306", new[]
        {
            new PowerEvent(Boot1, ShutdownKind.HardOff),
            new PowerEvent(Boot1.AddMinutes(2), ShutdownKind.HardOff),
        });

        await store.MergeJournalEventsAsync(report);
        var secondPass = await store.MergeJournalEventsAsync(report);

        Assert.Equal(0, secondPass.Count);
        Assert.Equal(2, (await store.GetRebootsAsync("260306")).Count);
    }

    [Fact]
    public async Task Store_MaintenanceWindow_TurnsEventIntoServiceWork()
    {
        // Регрессия (п.100): hard-off в простое переворачивал тактику, хотя питание в этот
        // момент снимали руками — и нигде это не было записано.
        var store = new SqliteSessionStore(Conn);
        await store.InitializeAsync();
        await store.RecordRebootAsync(new RebootEvent("160636", Boot2, Boot1, Boot2, 100, null,
            ShutdownKind.HardOff));

        await store.AddMaintenanceAsync(new MaintenanceWindow("160636",
            Boot2.AddMinutes(-30), Boot2.AddMinutes(30), "гасили стенд на ночь"));

        var timeline = await store.GetRebootsAsync("160636");

        var evt = Assert.Single(timeline.Events);
        Assert.Equal(ShutdownKind.Maintenance, evt.Kind);
        Assert.False(evt.IsFailure);
        Assert.Contains("гасили стенд", evt.ActivityBefore);
    }

    [Fact]
    public async Task Store_MaintenanceWindow_DoesNotTouchEventsOutsideIt()
    {
        var store = new SqliteSessionStore(Conn);
        await store.InitializeAsync();
        await store.RecordRebootAsync(new RebootEvent("160636", Boot2, Boot1, Boot2, 100, null,
            ShutdownKind.HardOff));

        await store.AddMaintenanceAsync(new MaintenanceWindow("160636",
            Boot2.AddHours(5), Boot2.AddHours(6), "перетыкали кабели"));

        var evt = Assert.Single((await store.GetRebootsAsync("160636")).Events);

        Assert.Equal(ShutdownKind.HardOff, evt.Kind);
        Assert.True(evt.IsFailure);
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
