namespace SzDiag.Hardware;

/// <summary>Шаг 2 кэш-паттерна: дорезолвить устройство, которого нет в локальной базе.
/// Реализация TPU отложена (Cloudflare/headless); пока — заглушка.</summary>
public interface IGpuScraper
{
    Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default);
}

/// <summary>Заглушка: скрапер ещё не подключён. Резолвер ловит это и отдаёт Unresolved.</summary>
public sealed class NotImplementedGpuScraper : IGpuScraper
{
    public Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default)
        => throw new NotSupportedException("TPU-скрапер ещё не подключён; обнови локальную базу через `hw update`");
}
