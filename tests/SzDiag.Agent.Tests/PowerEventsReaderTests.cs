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

    [Fact]
    public void Script_AlsoReadsPairedEventLog6008And6005()
    {
        // Регрессия (бэклог п.223, СЗ 161716): время отказа бралось из TimeCreated события 41,
        // которое пишется при СЛЕДУЮЩЕЙ загрузке. Парный 6008 несёт реальное время отказа в
        // своих Properties[0/1] (время/дата), а 6005 - момент старта сеанса для длительности.
        var script = PowerEventsReader.BuildScript(30);

        Assert.Contains("Id=6008", script);
        Assert.Contains("Id=6005", script);
    }

    [Fact]
    public void Parse_FourthField_IsUptimeBeforeSeconds()
    {
        var events = PowerEventsReader.Parse(
            Line("2026-08-05T13:00:58.0000000+00:00", "0", "0") + ";3600");

        Assert.Equal(3600, Assert.Single(events).UptimeBeforeSeconds);
        Assert.Equal(TimeSpan.FromHours(1), Assert.Single(events).UptimeBefore);
    }

    [Fact]
    public void Parse_WithoutFourthField_UptimeIsNull_BackwardCompatible()
    {
        // Старые записи/старая версия агента шлют только 3 поля - парсер не имеет права
        // падать или требовать четвёртое.
        var events = PowerEventsReader.Parse(Line("2026-08-05T13:00:58.0000000+00:00", "0", "0"));

        Assert.Null(Assert.Single(events).UptimeBeforeSeconds);
    }
}
