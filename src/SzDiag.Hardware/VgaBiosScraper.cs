using System.Web;

namespace SzDiag.Hardware;

/// <summary>Живой скрапер vgabios: по производителю+модели ищет прошивки, фетчит detail
/// кандидатов и матчит по Subsystem Id. Device-фоллбэк вне scope (заглушка).</summary>
public sealed class VgaBiosScraper : IGpuScraper
{
    private const string Base = "https://www.techpowerup.com";
    private readonly TechPowerUpClient _client;

    public VgaBiosScraper(TechPowerUpClient? client = null) => _client = client ?? new TechPowerUpClient();

    public Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default)
        => throw new NotSupportedException("device-фоллбэк vgabios не поддерживает");

    public async Task<ScrapedCard?> ScrapeCardAsync(PciId id, string? model, CancellationToken ct = default)
    {
        if (id.SubVendorId is null || id.SubDeviceId is null || string.IsNullOrWhiteSpace(model))
            return null;

        // Производителя карты берём из субвендора через vgabios? — нет, фильтруем поиск по модели,
        // производителя матчим уже по Subsystem-строке detail (надёжнее, чем угадывать имя фильтра).
        var searchUrl = $"{Base}/vgabios/?model={HttpUtility.UrlEncode(NormalizeModel(model))}";
        var searchHtml = await _client.GetHtmlAsync(searchUrl, ct);
        var rows = VgaBiosParser.ParseSearch(searchHtml);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var detailHtml = await _client.GetHtmlAsync(Base + row.DetailUrl, ct);
            var d = VgaBiosParser.ParseDetail(detailHtml);
            if (d.SubVendorId == id.SubVendorId && d.SubDeviceId == id.SubDeviceId)
            {
                return new ScrapedCard(
                    id.SubVendorId, id.SubDeviceId,
                    row.Manufacturer, string.IsNullOrEmpty(row.CardName) ? null : row.CardName,
                    d.MemorySize, d.MemoryType ?? row.MemoryType,
                    d.CoreClock, d.BoostClock, d.MemoryClock,
                    d.PowerTarget, d.PowerLimit, d.Outputs,
                    row.DateCompiled, d.VbiosVersion ?? row.VbiosVersion,
                    Base + row.DetailUrl);
            }
        }
        return null;   // subsystem-матча нет — честно «плату не определили»
    }

    // «GeForce RTX 5060 Ti» → «RTX 5060 Ti» (vgabios-модели без вендорного префикса)
    private static string NormalizeModel(string model) => model
        .Replace("GeForce ", "").Replace("Radeon ", "").Trim();
}
