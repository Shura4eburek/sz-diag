namespace SzDiag.Hardware;

/// <summary>Локальный справочник PCI-устройств (SQLite). Наполняется импортом pci.ids
/// и дозаписью из скрапера.</summary>
public interface IGpuRepository
{
    Task InitializeAsync(CancellationToken ct = default);
    Task ImportAsync(PciIdsData data, CancellationToken ct = default);
    Task<string?> LookupVendorAsync(string vendorId, CancellationToken ct = default);
    Task<PciDevice?> LookupDeviceAsync(string vendorId, string deviceId, CancellationToken ct = default);
    Task UpsertDeviceAsync(PciDevice device, CancellationToken ct = default);
}
