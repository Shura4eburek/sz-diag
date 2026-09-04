using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Характерный интервал между отказами — то, чем `close` должен был предупредить
/// на 160306: закрыли через 18 минут при характерном интервале ~53 часа, и CLI промолчал,
/// хотя все данные для сравнения уже лежали в SQLite (бэклог п.159).</summary>
public class RebootTimelineCharacteristicIntervalTests
{
    private static RebootEvent Failure(long uptimeSeconds) => new(
        "160306", DateTimeOffset.UtcNow, null, null, uptimeSeconds, null, ShutdownKind.HardOff);

    [Fact]
    public void CharacteristicInterval_NoFailures_ReturnsNull()
    {
        var timeline = new RebootTimeline("160306", Array.Empty<RebootEvent>(), null);
        Assert.Null(timeline.CharacteristicInterval);
    }

    [Fact]
    public void CharacteristicInterval_OneFailure_ReturnsItsUptime()
    {
        var timeline = new RebootTimeline("160306", new[] { Failure(53 * 3600) }, 53 * 3600);
        Assert.Equal(TimeSpan.FromHours(53), timeline.CharacteristicInterval);
    }

    [Fact]
    public void CharacteristicInterval_MultipleFailures_Averages()
    {
        var events = new[] { Failure(3600), Failure(7200) }; // 1ч и 2ч -> среднее 1.5ч
        var timeline = new RebootTimeline("160306", events, 7200);
        Assert.Equal(TimeSpan.FromHours(1.5), timeline.CharacteristicInterval);
    }

    [Fact]
    public void CharacteristicInterval_IgnoresBenignEvents()
    {
        // Кнопка/сон/плановое обесточивание не идут в наработку на отказ.
        var events = new[]
        {
            Failure(3600),
            new RebootEvent("160306", DateTimeOffset.UtcNow, null, null, 999999, null, ShutdownKind.PowerButton),
        };
        var timeline = new RebootTimeline("160306", events, 999999);
        Assert.Equal(TimeSpan.FromHours(1), timeline.CharacteristicInterval);
    }
}
