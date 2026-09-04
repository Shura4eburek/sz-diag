using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Hub видит только смены boot-time при живом heartbeat: на 160636 настоящий hard-off
/// 05.08 16:00:58 в таймлайн не попал вовсе, и `reboots` печатал «не зафиксировано» (п.97).</summary>
public class PowerEventsReaderTests
{
    private static string Line(string time, string bugcheck, string powerButton)
        => $"{time};{bugcheck};{powerButton}";

    [Fact]
    public void Parse_ClassifiesEachEvent()
    {
        var stdout = string.Join("\n", new[]
        {
            Line("2026-08-05T13:00:58.0000000+00:00", "0", "0"),
            Line("2026-08-04T11:30:06.0000000+00:00", "0", "134297341459381656"),
            Line("2026-07-28T20:35:54.0000000+00:00", "190", "0"),
        });

        var events = PowerEventsReader.Parse(stdout);

        Assert.Equal(3, events.Count);
        Assert.Equal(ShutdownKind.HardOff, events[0].Kind);
        Assert.Equal(ShutdownKind.PowerButton, events[1].Kind);
        Assert.Equal(ShutdownKind.Bsod, events[2].Kind);
    }

    [Fact]
    public void Parse_KeepsBugcheckCode()
    {
        // Регрессия (бэклог п.121): код читался из события и тут же выбрасывался —
        // «BSOD ×13» не разделяет один почерк и три разных дефекта.
        var events = PowerEventsReader.Parse(Line("2026-07-28T20:35:54.0000000+00:00", "239", "0"));

        Assert.Equal(239, Assert.Single(events).Bugcheck);
    }

    [Fact]
    public void Parse_GarbageLines_AreSkipped()
    {
        var events = PowerEventsReader.Parse("мусор\n\n2026-08-05T13:00:58.0000000+00:00;0;0\nещё мусор");

        Assert.Single(events);
    }

    [Fact]
    public void Script_ReadsKernelPower41WithinWindow()
    {
        var script = PowerEventsReader.BuildScript(30);

        Assert.Contains("Id=41", script);
        Assert.Contains("AddDays(-30)", script);
        Assert.Contains("PowerButtonTimestamp", script);
        Assert.All(script, c => Assert.True(c < 128, "тело скрипта — строго ASCII"));
    }

    // Сон при живой сессии (бэклог п.140/222): Kernel-Power 42 -> 107, без него наработка
    // опиралась на голый аптайм, а сутки «наблюдения» на 161346 оказались 7 часами работы.
    [Fact]
    public void Script_ReadsKernelPower42And107ForSleep()
    {
        var script = PowerEventsReader.BuildScript(30);

        Assert.Contains("Id=42,107", script);
        Assert.Contains("SLEEP;", script);
        Assert.All(script, c => Assert.True(c < 128, "тело скрипта — строго ASCII"));
    }

    [Fact]
    public void Parse_SleepLine_ProducesSleepEventWithDuration()
    {
        var events = PowerEventsReader.Parse("SLEEP;2026-08-10T19:32:00.0000000+00:00;60300");

        var evt = Assert.Single(events);
        Assert.Equal(ShutdownKind.Sleep, evt.Kind);
        Assert.Equal(60300, evt.DurationSeconds);
        Assert.Equal(new DateTimeOffset(2026, 8, 10, 19, 32, 0, TimeSpan.Zero), evt.At);
    }

    [Fact]
    public void Parse_MixesHardOffAndSleepLines()
    {
        var stdout = string.Join("\n", new[]
        {
            Line("2026-08-05T13:00:58.0000000+00:00", "0", "0"),
            "SLEEP;2026-08-10T19:32:00.0000000+00:00;60300",
        });

        var events = PowerEventsReader.Parse(stdout);

        Assert.Equal(2, events.Count);
        Assert.Equal(ShutdownKind.HardOff, events[0].Kind);
        Assert.Equal(ShutdownKind.Sleep, events[1].Kind);
    }
}
