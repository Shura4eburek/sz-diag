using System.Net.Http;

namespace SzDiag.Hardware;

/// <summary>Единственное место с сетью к TPU. Тянет HTML браузерным UA и ловит bot-challenge.
/// Парсинг — в VgaBiosParser (чистый, тестируется на фикстурах).</summary>
public sealed class TechPowerUpClient
{
    private const string Ua =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private static readonly string[] ChallengeMarkers =
        { "Automated bot check", "Drag the handle", "challenge-platform" };

    private readonly HttpClient _http;

    public TechPowerUpClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(Ua))
            _http.DefaultRequestHeaders.Add("User-Agent", Ua);
    }

    /// <summary>GET страницы TPU. Кидает <see cref="ScrapeBlockedException"/>, если это challenge.</summary>
    public async Task<string> GetHtmlAsync(string url, CancellationToken ct = default)
    {
        var html = await _http.GetStringAsync(url, ct);
        EnsureNotBlocked(html);
        return html;
    }

    /// <summary>Проверка HTML на маркеры bot-challenge. Статик — чтобы тестировать на фикстуре.</summary>
    public static void EnsureNotBlocked(string html)
    {
        foreach (var m in ChallengeMarkers)
            if (html.Contains(m, StringComparison.OrdinalIgnoreCase))
                throw new ScrapeBlockedException($"TPU вернул bot-challenge (маркер: «{m}»)");
    }
}
