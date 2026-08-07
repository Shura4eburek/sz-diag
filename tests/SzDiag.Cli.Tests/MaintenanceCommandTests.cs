using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Вечернее выключение стенда в журнале клиента неотличимо от дефекта, и событие
/// зависает: либо ключевая улика, либо шум (бэклог п.100).</summary>
public class MaintenanceCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 20, 15, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Parse_WithoutTimes_UsesWindowAroundNow()
    {
        var parsed = MaintenanceCommand.Parse(
            new[] { "maintenance", "160636", "выключали стенд на ночь" }, Now);

        Assert.NotNull(parsed);
        Assert.Equal("160636", parsed!.Sz);
        Assert.Equal("выключали стенд на ночь", parsed.Reason);
        Assert.Equal(Now - MaintenanceCommand.DefaultHalfWindow, parsed.From);
        Assert.Equal(Now + MaintenanceCommand.DefaultHalfWindow, parsed.Until);
    }

    [Fact]
    public void Parse_WithExplicitTimes_TakesThem()
    {
        var parsed = MaintenanceCommand.Parse(
            new[] { "maintenance", "160636", "рубильник", "--from", "18:30", "--until", "19:15" }, Now);

        Assert.Equal(18, parsed!.From.Hour);
        Assert.Equal(30, parsed.From.Minute);
        Assert.Equal(19, parsed.Until.Hour);
        Assert.Equal("рубильник", parsed.Reason);
    }

    [Fact]
    public void ParseTime_UnderstandsOperatorFormats()
    {
        Assert.Equal(new DateTimeOffset(2026, 8, 6, 18, 30, 0, TimeSpan.FromHours(3)),
            MaintenanceCommand.ParseTime("18:30", Now));
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 16, 0, 0, TimeSpan.FromHours(3)),
            MaintenanceCommand.ParseTime("05.08 16:00", Now));
        Assert.Null(MaintenanceCommand.ParseTime("вчера вечером", Now));
    }

    [Fact]
    public void Parse_ListFlag_IsRecognised()
    {
        var parsed = MaintenanceCommand.Parse(new[] { "maintenance", "160636", "--list" }, Now);

        Assert.True(parsed!.List);
    }

    [Fact]
    public void Window_CoversEventInside_AndNotOutside()
    {
        var w = new MaintenanceWindow("160636", Now.AddHours(-1), Now.AddHours(1), "гасили рубильником");

        Assert.True(w.Covers(Now));
        Assert.False(w.Covers(Now.AddHours(2)));
        Assert.True(w.IsActive(Now));
    }

    [Fact]
    public void MaintenanceKind_IsNotAFailure()
    {
        Assert.False(ShutdownKind.CountsAsFailure(ShutdownKind.Maintenance));
        Assert.Equal("обслуживание", ShutdownKind.Describe(ShutdownKind.Maintenance));
    }
}
