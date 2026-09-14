using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Имя quick tunnel'а печатается только в лог — другого способа его узнать нет,
/// поэтому разбор обязан быть устойчивым. Фикстуры — настоящий вывод cloudflared 2026.8.2,
/// снятый при проверке схемы на боксе 2026-09-14.</summary>
public class CloudflaredOutputParserTests
{
    private const string РеальныйЛог = """
2026-09-14T13:07:36Z INF Requesting new quick Tunnel on trycloudflare.com...
2026-09-14T13:07:41Z INF +--------------------------------------------------------------+
2026-09-14T13:07:41Z INF |  Your quick Tunnel has been created! Visit it at              |
2026-09-14T13:07:41Z INF |  https://pending-values-phil-alike.trycloudflare.com          |
2026-09-14T13:07:41Z INF +--------------------------------------------------------------+
2026-09-14T13:07:41Z INF Settings: map[ha-connections:1 url:ssh://localhost:22]
""";

    [Fact]
    public void Достаёт_имя_из_рамки()
    {
        var ok = CloudflaredOutputParser.TryFindHostname(РеальныйЛог, out var host);

        Assert.True(ok);
        Assert.Equal("pending-values-phil-alike.trycloudflare.com", host);
    }

    [Fact]
    public void Схема_и_рамка_в_имя_не_попадают()
    {
        CloudflaredOutputParser.TryFindHostname(РеальныйЛог, out var host);

        Assert.DoesNotContain("https", host);
        Assert.DoesNotContain("|", host);
        Assert.DoesNotContain(" ", host);
    }

    [Fact]
    public void Пока_имени_нет_возвращает_false()
    {
        // Строка про "Requesting new quick Tunnel on trycloudflare.com" появляется РАНЬШЕ
        // имени и сама содержит домен — принять её за имя нельзя.
        var частичный = "2026-09-14T13:07:36Z INF Requesting new quick Tunnel on trycloudflare.com...";

        Assert.False(CloudflaredOutputParser.TryFindHostname(частичный, out var host));
        Assert.Null(host);
    }

    [Fact]
    public void Пустой_лог_не_валит_разбор()
    {
        Assert.False(CloudflaredOutputParser.TryFindHostname("", out _));
    }

    [Fact]
    public void Берёт_последнее_имя_если_туннель_переподнимали()
    {
        var дважды = РеальныйЛог + "\n" +
            "2026-09-14T14:00:00Z INF |  https://second-name-here.trycloudflare.com  |";

        CloudflaredOutputParser.TryFindHostname(дважды, out var host);

        Assert.Equal("second-name-here.trycloudflare.com", host);
    }

    [Fact]
    public void Имя_с_цифрами_и_дефисами_разбирается()
    {
        var лог = "INF |  https://abc-123-def-45.trycloudflare.com  |";

        Assert.True(CloudflaredOutputParser.TryFindHostname(лог, out var host));
        Assert.Equal("abc-123-def-45.trycloudflare.com", host);
    }
}
