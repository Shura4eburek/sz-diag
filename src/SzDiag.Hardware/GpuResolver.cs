namespace SzDiag.Hardware;

/// <summary>Откуда взялась модель устройства.</summary>
public enum GpuSource { Cache, Scraper, Unresolved }

/// <summary>Результат резолва PCI ID. Вендор/партнёр могут быть известны даже при Unresolved.</summary>
public sealed record GpuResolution(
    string VendorId, string? VendorName,
    string DeviceId, string? DeviceName, string? Chip, string? Model,
    string? SubVendorId, string? SubVendorName,
    string? Revision, GpuSource Source);

/// <summary>Оркестрация кэш-паттерна: БД → miss → скрапер → запись. Резолвит вендора,
/// устройство и партнёра независимо — при device-miss отдаёт что известно.</summary>
public sealed class GpuResolver
{
    private readonly IGpuRepository _repo;
    private readonly IGpuScraper _scraper;

    public GpuResolver(IGpuRepository repo, IGpuScraper scraper)
    {
        _repo = repo;
        _scraper = scraper;
    }

    public async Task<GpuResolution> ResolveAsync(PciId id, CancellationToken ct = default)
    {
        var vendorName = await _repo.LookupVendorAsync(id.VendorId, ct);
        var subVendorName = id.SubVendorId is null ? null : await _repo.LookupVendorAsync(id.SubVendorId, ct);

        var device = await _repo.LookupDeviceAsync(id.VendorId, id.DeviceId, ct);
        var source = GpuSource.Cache;

        if (device is null)
        {
            source = GpuSource.Unresolved;
            try
            {
                var scraped = await _scraper.ScrapeAsync(id, ct);
                if (scraped is not null)
                {
                    await _repo.UpsertDeviceAsync(scraped, ct);
                    device = scraped;
                    source = GpuSource.Scraper;
                }
            }
            catch (NotSupportedException) { /* заглушка — остаёмся Unresolved */ }
        }

        return new GpuResolution(
            id.VendorId, vendorName,
            id.DeviceId, device?.Name, device?.Chip, device?.Model,
            id.SubVendorId, subVendorName,
            id.Revision, source);
    }
}
