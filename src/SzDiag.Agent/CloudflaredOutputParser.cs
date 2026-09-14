using System.Text.RegularExpressions;

namespace SzDiag.Agent;

/// <summary>Разбор вывода `cloudflared tunnel --url ...`. Имя quick tunnel'а печатается
/// только в лог, отдельного способа его узнать нет — приходится вылавливать.</summary>
public static partial class CloudflaredOutputParser
{
    /// <summary>Имя выдаётся внутри ASCII-рамки как ссылка: `|  https://&lt;имя&gt;  |`.
    /// Схема `https://` в шаблоне обязательна: строка «Requesting new quick Tunnel on
    /// trycloudflare.com...» печатается РАНЬШЕ и сама содержит домен — без привязки к схеме
    /// разбор принимал бы её за готовое имя и отдавал бы мусор вместо адреса.</summary>
    [GeneratedRegex(@"https://([a-z0-9-]+\.trycloudflare\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex HostnameRegex();

    /// <summary>Последнее встреченное имя: при переподнятии туннеля в одном логе окажется
    /// несколько, актуально последнее.</summary>
    public static bool TryFindHostname(string log, out string? hostname)
    {
        hostname = null;
        if (string.IsNullOrEmpty(log)) return false;

        var matches = HostnameRegex().Matches(log);
        if (matches.Count == 0) return false;

        hostname = matches[^1].Groups[1].Value;
        return true;
    }
}
