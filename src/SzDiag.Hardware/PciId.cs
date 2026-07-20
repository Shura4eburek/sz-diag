using System.Text.RegularExpressions;

namespace SzDiag.Hardware;

/// <summary>Разобранный Windows PCI hardware ID. VendorId/DeviceId обязательны,
/// остальное опционально. Все id — lowercase hex без префиксов.</summary>
public sealed record PciId(
    string VendorId, string DeviceId,
    string? SubVendorId = null, string? SubDeviceId = null, string? Revision = null)
{
    /// <summary>Разбирает строку вида
    /// PCI\VEN_10DE&amp;DEV_2D04&amp;SUBSYS_53621462&amp;REV_A1\...
    /// SUBSYS: младшее слово — субвендор, старшее — субустройство (формат Windows).</summary>
    public static PciId Parse(string raw)
    {
        var ven = Match(raw, "VEN_([0-9A-Fa-f]{4})");
        var dev = Match(raw, "DEV_([0-9A-Fa-f]{4})");
        if (ven is null || dev is null)
            throw new FormatException($"не PCI hardware ID: «{raw}»");

        string? subVen = null, subDev = null;
        var subsys = Match(raw, "SUBSYS_([0-9A-Fa-f]{8})");
        if (subsys is not null)
        {
            subDev = subsys.Substring(0, 4);
            subVen = subsys.Substring(4, 4);
        }
        return new PciId(ven, dev, subVen, subDev, Match(raw, "REV_([0-9A-Fa-f]{2})"));
    }

    private static string? Match(string input, string pattern)
    {
        var m = Regex.Match(input, pattern);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }
}
