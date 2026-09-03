using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>`sensors start` раньше молча перезатирал состояние прошлого прогона на хосте —
/// CSV не терялся (у каждого прогона свой файл с таймстампом), но CLI переставал видеть job
/// прошлого прогона и не мог его ни остановить, ни показать статус (бэклог п.145).</summary>
public class SensorsCommandTests
{
    [Fact]
    public void PreviousRunWarning_NoPreviousRun_ReturnsNull()
        => Assert.Null(SensorsCommand.PreviousRunWarning(null, null, null));

    [Fact]
    public void PreviousRunWarning_PreviousRunExists_MentionsJobAndCsv()
    {
        var warning = SensorsCommand.PreviousRunWarning(
            "job-1", @"C:\ProgramData\szdiag\sensors\156864-20260904-100000.csv",
            new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));

        Assert.NotNull(warning);
        Assert.Contains("job-1", warning);
        Assert.Contains("156864-20260904-100000.csv", warning);
    }

    // #156/бэклог п.206: под нагрузкой процесс наблюдателя жив, а CSV не растёт 18 минут —
    // `status` при этом рапортовал «идёт». Хартбит в stdout — способ увидеть это без сети.
    [Fact]
    public void ParseHeartbeatRows_NoTickLines_ReturnsNull()
        => Assert.Null(SensorsCommand.ParseHeartbeatRows("some other output\nno ticks here"));

    [Fact]
    public void ParseHeartbeatRows_TakesLastTickLine()
    {
        var tail = "tick;1;2026-09-04 13:27:45\ntick;2;2026-09-04 13:27:57\ntick;3;2026-09-04 13:28:08";

        Assert.Equal(3, SensorsCommand.ParseHeartbeatRows(tail));
    }

    [Fact]
    public void IsStale_RunningButNoOutputForOver3Intervals_IsTrue()
    {
        var now = new DateTimeOffset(2026, 8, 24, 13, 46, 4, TimeSpan.Zero);
        var lastOutput = new DateTimeOffset(2026, 8, 24, 13, 28, 8, TimeSpan.Zero);   // кейс 161716: дыра 18 минут

        Assert.True(SensorsCommand.IsStale(running: true, lastOutput, intervalSeconds: 10, now));
    }

    [Fact]
    public void IsStale_RunningAndFresh_IsFalse()
    {
        var now = new DateTimeOffset(2026, 8, 24, 13, 28, 12, TimeSpan.Zero);
        var lastOutput = new DateTimeOffset(2026, 8, 24, 13, 28, 8, TimeSpan.Zero);

        Assert.False(SensorsCommand.IsStale(running: true, lastOutput, intervalSeconds: 10, now));
    }

    [Fact]
    public void IsStale_NotRunning_IsFalse()
    {
        // «Завершён» — отдельный, честный статус; «не пишет» относится только к живому процессу.
        var now = DateTimeOffset.UtcNow;
        Assert.False(SensorsCommand.IsStale(running: false, now.AddMinutes(-30), 10, now));
    }

    [Fact]
    public void FreshnessLine_NoHeartbeatYet_SaysUnknown()
        => Assert.Contains("хартбит", SensorsCommand.FreshnessLine(null, null, DateTimeOffset.UtcNow));

    [Fact]
    public void FreshnessLine_WithData_MentionsRowsAndLag()
    {
        var now = new DateTimeOffset(2026, 9, 4, 10, 5, 0, TimeSpan.Zero);
        var line = SensorsCommand.FreshnessLine(42, new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero), now);

        Assert.Contains("42 строк", line);
        Assert.Contains("5,0 мин назад", line.Replace('.', ','));
    }

    // #139/бэклог п.190: наблюдатель, поднятый рецептом start-sensors.ps1 (задача
    // szdiag-lhm-<СЗ>, процесс lhmmon), не виден hub'у — status/stop должны узнавать его
    // по факту на клиенте, а не только по своей записи о старте.
    [Fact]
    public void ParseProbe_ValidJson_ParsesAllFields()
    {
        var json = "{\"ProcessAlive\":true,\"TaskState\":\"Running\",\"CsvExists\":true," +
                   "\"Rows\":120,\"LastWrite\":\"2026-08-20T10:05:00+03:00\"}";

        var probe = SensorsCommand.ParseProbe(json);

        Assert.NotNull(probe);
        Assert.True(probe!.ProcessAlive);
        Assert.Equal("Running", probe.TaskState);
        Assert.True(probe.CsvExists);
        Assert.Equal(120, probe.Rows);
        Assert.NotNull(probe.LastWrite);
    }

    [Fact]
    public void ParseProbe_EmptyOutput_ReturnsNull()
        => Assert.Null(SensorsCommand.ParseProbe(""));

    [Fact]
    public void ParseProbe_Garbage_ReturnsNullNotThrows()
        => Assert.Null(SensorsCommand.ParseProbe("this is not json"));

    [Fact]
    public void ParseProbe_JsonOnLastLine_IgnoresNoiseBefore()
    {
        var stdout = "WARNING: some schtasks noise\n" +
                     "{\"ProcessAlive\":false,\"TaskState\":null,\"CsvExists\":false,\"Rows\":null,\"LastWrite\":null}";

        var probe = SensorsCommand.ParseProbe(stdout);

        Assert.NotNull(probe);
        Assert.False(probe!.ProcessAlive);
    }

    [Fact]
    public void ProbeFoundWatcher_AllFalseAndNoTask_IsFalse()
    {
        var probe = new SensorsCommand.ClientSensorProbe(false, null, false, null, null);
        Assert.False(SensorsCommand.ProbeFoundWatcher(probe));
    }

    [Fact]
    public void ProbeFoundWatcher_ProcessAlive_IsTrue()
    {
        var probe = new SensorsCommand.ClientSensorProbe(true, null, false, null, null);
        Assert.True(SensorsCommand.ProbeFoundWatcher(probe));
    }

    [Fact]
    public void ProbeFoundWatcher_TaskStateSet_IsTrue()
    {
        // Ровно кейс 160705: задача поднята и RUNNING, а процесс/CSV разведка ещё не проверила.
        var probe = new SensorsCommand.ClientSensorProbe(false, "Running", false, null, null);
        Assert.True(SensorsCommand.ProbeFoundWatcher(probe));
    }

    [Fact]
    public void FormatProbe_MentionsTaskProcessAndCsv()
    {
        var probe = new SensorsCommand.ClientSensorProbe(true, "Running", true, 42,
            new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        var now = new DateTimeOffset(2026, 8, 20, 10, 1, 0, TimeSpan.Zero);

        var line = SensorsCommand.FormatProbe(probe, now);

        Assert.Contains("Running", line);
        Assert.Contains("процесс жив", line);
        Assert.Contains("42 строк", line);
    }
}
