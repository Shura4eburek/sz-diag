namespace SzDiag.Hardware;

/// <summary>Дорезолв из внешнего источника (TPU). Реализация — VgaBiosScraper.</summary>
public interface IGpuScraper
{
    /// <summary>Device-модель, которой нет в pci.ids. Вне scope — остаётся заглушкой.</summary>
    Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default);

    /// <summary>Точная плата + спеки по subsystem из vgabios. model — имя из pci.ids для поиска.</summary>
    Task<ScrapedCard?> ScrapeCardAsync(PciId id, string? model, CancellationToken ct = default);
}

/// <summary>Заглушка: живой скрапер не подключён. Резолвер ловит и отдаёт без дорезолва.</summary>
public sealed class NotImplementedGpuScraper : IGpuScraper
{
    public Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default)
        => throw new NotSupportedException("TPU-скрапер device-модели не подключён; обнови базу через `hw update`");

    public Task<ScrapedCard?> ScrapeCardAsync(PciId id, string? model, CancellationToken ct = default)
        => throw new NotSupportedException("TPU vgabios-скрапер не подключён");
}
