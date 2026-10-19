using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Порядок поиска hub: постоянный домен из конфига, при неудаче — UDP-broadcast по
/// локалке. Broadcast не выбрасывается вместе с переездом на домен: он остаётся рабочим
/// путём, когда интернета нет, а машина стоит в одной сети с боксом.</summary>
public class HubResolverTests
{
    [Fact]
    public async Task Домен_из_конфига_идёт_первым()
    {
        var (url, byBroadcast) = await HubResolver.ResolveAsync("https://hub.example.com",
            () => throw new InvalidOperationException("broadcast звать не должны"));

        Assert.Equal("https://hub.example.com", url);
        Assert.False(byBroadcast);
    }

    [Fact]
    public async Task Пустой_конфиг_уходит_в_broadcast()
    {
        var (url, byBroadcast) = await HubResolver.ResolveAsync(null,
            () => Task.FromResult("http://192.168.1.10:5000"));

        Assert.Equal("http://192.168.1.10:5000", url);
        Assert.True(byBroadcast);
    }

    [Fact]
    public async Task Домен_недоступен_откатываемся_на_broadcast()
    {
        // Cloudflare лёг целиком: для машины в LAN бокса это полный обход.
        var (url, byBroadcast) = await HubResolver.ResolveAsync("https://hub.example.com",
            () => Task.FromResult("http://192.168.1.10:5000"),
            probe: _ => Task.FromResult(false));

        Assert.Equal("http://192.168.1.10:5000", url);
        Assert.True(byBroadcast);
    }

    [Fact]
    public async Task Оба_пути_мертвы_бросаем_HubNotFound()
    {
        await Assert.ThrowsAsync<HubNotFoundException>(() => HubResolver.ResolveAsync(
            "https://hub.example.com",
            () => throw new HubNotFoundException("нет hub"),
            probe: _ => Task.FromResult(false)));
    }

    [Fact]
    public async Task Пробник_не_задан_домен_принимается_как_есть()
    {
        // Реальную доступность проверит само подключение — лишний round-trip на старте
        // агента не нужен.
        var (url, byBroadcast) = await HubResolver.ResolveAsync("https://hub.example.com",
            () => Task.FromResult("http://192.168.1.10:5000"));

        Assert.Equal("https://hub.example.com", url);
        Assert.False(byBroadcast);
    }
}
