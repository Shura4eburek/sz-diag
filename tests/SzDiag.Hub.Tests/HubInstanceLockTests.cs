using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>19.10: второй hub на том же порту успевал убить cloudflared живого — теперь он не стартует.</summary>
public class HubInstanceLockTests
{
    // Порты вне рабочих (5099): тесты не должны мешать живому hub на боксе и друг другу.
    [Fact]
    public void SecondAcquire_SamePort_Refused_UntilFirstReleased()
    {
        var first = HubInstanceLock.TryAcquire(45901);
        Assert.NotNull(first);
        Assert.Null(HubInstanceLock.TryAcquire(45901));

        first!.Dispose();
        using var again = HubInstanceLock.TryAcquire(45901);
        Assert.NotNull(again);
    }

    [Fact]
    public void DifferentPorts_Independent()
    {
        using var a = HubInstanceLock.TryAcquire(45902);
        using var b = HubInstanceLock.TryAcquire(45903);
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5099", 5099)]
    [InlineData("http://*:6000", 6000)]
    [InlineData("http://+:7000", 7000)]
    [InlineData("http://localhost:5100;http://0.0.0.0:5200", 5100)]
    public void PortOf_FirstListenUrl(string urls, int expected)
        => Assert.Equal(expected, HubInstanceLock.PortOf(urls.Split(';')));
}
