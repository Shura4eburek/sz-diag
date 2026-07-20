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
}
