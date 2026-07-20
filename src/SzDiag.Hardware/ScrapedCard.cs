namespace SzDiag.Hardware;

/// <summary>Партнёрская плата + спеки из TPU VGA BIOS. Ключ — subsystem (subven/subdev).</summary>
public sealed record ScrapedCard(
    string SubVendorId, string SubDeviceId,
    string? Manufacturer, string? CardName,
    string? MemorySize, string? MemoryType,
    string? CoreClock, string? BoostClock, string? MemoryClock,
    string? PowerTarget, string? PowerLimit,
    string? Outputs, string? DateCompiled, string? VbiosVersion,
    string SourceUrl);
