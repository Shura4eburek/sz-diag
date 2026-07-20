using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace SzDiag.Hardware;

/// <summary>Строка search-списка vgabios: производитель, торговое имя карты, ссылка на detail.</summary>
public sealed record VgaBiosRow(
    string Manufacturer, string Model, string CardName, string DetailUrl,
    string? DateCompiled, string? VbiosVersion, string? MemoryType);

/// <summary>Разобранная detail-страница прошивки.</summary>
public sealed record VgaBiosDetail(
    string? SubVendorId, string? SubDeviceId,
    string? MemorySize, string? MemoryType,
    string? CoreClock, string? BoostClock, string? MemoryClock,
    string? PowerTarget, string? PowerLimit, string? Outputs,
    string? VbiosVersion);

/// <summary>Чистый парсер HTML vgabios (AngleSharp, без сети). Публичный статик — как PciIdsParser.</summary>
public static class VgaBiosParser
{
    private static readonly HtmlParser Html = new();

    public static IReadOnlyList<VgaBiosRow> ParseSearch(string html)
    {
        var doc = Html.ParseDocument(html);
        var rows = new List<VgaBiosRow>();
        foreach (var tr in doc.QuerySelectorAll("table.bioslist tbody tr"))
        {
            var link = tr.QuerySelector("td.name a");
            if (link is null) continue;
            var url = link.GetAttribute("href") ?? "";
            var mfgr = tr.QuerySelector("td.mfgr")?.TextContent.Trim() ?? "";
            var model = link.TextContent.Trim();
            var cardName = tr.QuerySelector("td.name div.cardname")?.TextContent.Trim() ?? "";
            var tds = tr.QuerySelectorAll("td");
            // колонки: 0=mfgr 1=name 2=Date compiled 3=Version 4=Interface 5=Core/Mem/Boost 6=Memory 7=Links
            string? Cell(int i) => tds.Length > i ? tds[i].TextContent.Trim() : null;
            var date = Cell(2)?.Split(' ')[0];                 // «2025-03-15 00:00:00» → «2025-03-15»
            rows.Add(new VgaBiosRow(mfgr, model, cardName, url, date, Cell(3), Cell(6)));
        }
        return rows;
    }

    public static VgaBiosDetail ParseDetail(string html)
    {
        var doc = Html.ParseDocument(html);

        // Таблица «Graphics Card Info»: <tr><th>Label:</th><td>Value</td></tr>
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tr in doc.QuerySelectorAll("table tr"))
        {
            var th = tr.QuerySelector("th");
            var td = tr.QuerySelector("td");
            if (th is null || td is null) continue;
            var key = th.TextContent.Trim().TrimEnd(':').Trim();
            if (!map.ContainsKey(key)) map[key] = td.TextContent.Trim();
        }
        string? Get(string k) => map.TryGetValue(k, out var v) && v.Length > 0 ? v : null;

        // Subsystem Id: «1462 5351» → subven / subdev (lowercase hex)
        string? subVen = null, subDev = null;
        var sub = Get("Subsystem Id");
        if (sub is not null)
        {
            var parts = sub.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) { subVen = parts[0].ToLowerInvariant(); subDev = parts[1].ToLowerInvariant(); }
        }

        // Свободный VBIOS-блок: выходы и лимиты мощности — регулярками по тексту тела.
        // Singleline: блок «Connectors» — многострочный список, точка должна перекрывать переводы строк.
        var body = doc.Body?.TextContent ?? "";
        static string? Rx(string text, string pattern) =>
            System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.Singleline) is { Success: true } m
                ? m.Groups[1].Value.Trim() : null;

        var outputs = Rx(body, @"Connectors\s+(.+?)\s+Board power limit");
        var target = Rx(body, @"Target:\s*([\d.]+\s*W)");
        var limit  = Rx(body, @"Limit:\s*([\d.]+\s*W)");

        return new VgaBiosDetail(
            subVen, subDev,
            Get("Memory Size"), Get("Memory Type"),
            Get("GPU Clock"), Get("Boost Clock"), Get("Memory Clock"),
            target, limit, outputs,
            Get("VBIOS Version"));
    }
}
