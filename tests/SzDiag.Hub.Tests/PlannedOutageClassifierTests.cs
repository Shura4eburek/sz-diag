using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Плановое обесточивание сервиса (рубильник на ночь) не должно засчитываться как
/// дефект машины (бэклог п.130, СЗ 161346).</summary>
public class PlannedOutageClassifierTests
{
    [Fact]
    public void IsOutsideServiceHours_BeforeStart_ReturnsTrue()
    {
        var at = new TimeOnly(6, 0);
        Assert.True(PlannedOutageClassifier.IsOutsideServiceHours(at, new TimeOnly(8, 0), new TimeOnly(19, 0)));
    }

    [Fact]
    public void IsOutsideServiceHours_AfterEnd_ReturnsTrue()
    {
        var at = new TimeOnly(22, 30);
        Assert.True(PlannedOutageClassifier.IsOutsideServiceHours(at, new TimeOnly(8, 0), new TimeOnly(19, 0)));
    }

    [Fact]
    public void IsOutsideServiceHours_WithinWindow_ReturnsFalse()
    {
        var at = new TimeOnly(14, 0);
        Assert.False(PlannedOutageClassifier.IsOutsideServiceHours(at, new TimeOnly(8, 0), new TimeOnly(19, 0)));
    }

    [Fact]
    public void IsOutsideServiceHours_NoWindowConfigured_NeverOutside()
    {
        // Рубильник расписания выключен по умолчанию — не мешаем конфигам без Hub.ServiceHours*.
        Assert.False(PlannedOutageClassifier.IsOutsideServiceHours(new TimeOnly(3, 0), null, null));
    }

    [Fact]
    public void IsOutsideServiceHours_OvernightWindow_WrapsMidnight()
    {
        // Ночная смена сервиса: окно 20:00-06:00.
        var start = new TimeOnly(20, 0);
        var end = new TimeOnly(6, 0);
        Assert.False(PlannedOutageClassifier.IsOutsideServiceHours(new TimeOnly(23, 0), start, end));
        Assert.True(PlannedOutageClassifier.IsOutsideServiceHours(new TimeOnly(12, 0), start, end));
    }

    [Fact]
    public void IsPlanned_MassOfflineInsideServiceHours_StillPlanned()
    {
        // Признак 2 не зависит от расписания: свет пропал у всех разом среди дня.
        var at = new TimeOnly(14, 0);
        Assert.True(PlannedOutageClassifier.IsPlanned(at, new TimeOnly(8, 0), new TimeOnly(19, 0), massOffline: true));
    }

    [Fact]
    public void IsPlanned_SingleMachineInsideServiceHours_NotPlanned()
    {
        var at = new TimeOnly(14, 0);
        Assert.False(PlannedOutageClassifier.IsPlanned(at, new TimeOnly(8, 0), new TimeOnly(19, 0), massOffline: false));
    }
}
