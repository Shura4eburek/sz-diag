using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Регрессия (бэклог п.159, СЗ 160306): закрыли через 18 минут при характерном
/// интервале дефекта ~53 часа, и `close` про это ничего не сказал.</summary>
public class ObservationSufficiencyTests
{
    [Fact]
    public void Warn_ObservationShorterThanInterval_ReturnsWarning()
    {
        var msg = ObservationSufficiency.Warn(TimeSpan.FromMinutes(18), TimeSpan.FromHours(53));

        Assert.NotNull(msg);
        Assert.Contains("18 мин", msg);
        Assert.Contains("53 ч", msg);
    }

    [Fact]
    public void Warn_ObservationLongerThanInterval_ReturnsNull()
        => Assert.Null(ObservationSufficiency.Warn(TimeSpan.FromHours(60), TimeSpan.FromHours(53)));

    [Fact]
    public void Warn_ObservationEqualsInterval_ReturnsNull()
        => Assert.Null(ObservationSufficiency.Warn(TimeSpan.FromHours(53), TimeSpan.FromHours(53)));

    [Fact]
    public void Warn_NoHistoryYet_ReturnsNull()
        => Assert.Null(ObservationSufficiency.Warn(TimeSpan.FromMinutes(5), null));
}
