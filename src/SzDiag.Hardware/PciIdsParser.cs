using System.Text.RegularExpressions;

namespace SzDiag.Hardware;

/// <summary>Устройство из pci.ids. Chip — текст до «[», Model — внутри «[...]» (если есть).</summary>
public sealed record PciDevice(string VendorId, string DeviceId, string Name, string? Chip, string? Model);

/// <summary>Разобранный pci.ids: вендоры (id → имя) и устройства.</summary>
public sealed record PciIdsData(IReadOnlyDictionary<string, string> Vendors, IReadOnlyList<PciDevice> Devices);

/// <summary>
/// Парсер формата pci.ids. Вендор — строка без отступа «id  name»; устройство — с одним
/// табом; субсистема — с двумя (игнорируется). Строки с «#» и пустые пропускаются.
/// </summary>
public static class PciIdsParser
{
    public static PciIdsData Parse(string text)
    {
        var vendors = new Dictionary<string, string>();
        var devices = new List<PciDevice>();
        string? currentVendor = null;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.Length == 0 || rawLine.TrimStart().StartsWith("#")) continue;
            if (rawLine.StartsWith("\t\t")) continue;                 // субсистема — не нужна

            if (rawLine.StartsWith("\t"))                             // устройство
            {
                if (currentVendor is null) continue;
                var (id, name) = SplitIdName(rawLine.Substring(1));
                if (id is null) continue;
                var (chip, model) = SplitChipModel(name!);
                devices.Add(new PciDevice(currentVendor, id, name!, chip, model));
            }
            else if (!char.IsWhiteSpace(rawLine[0]))                 // вендор
            {
                var (id, name) = SplitIdName(rawLine);
                if (id is null) continue;
                currentVendor = id;
                vendors[id] = name!;
            }
        }
        return new PciIdsData(vendors, devices);
    }

    // «10de  NVIDIA Corporation» → ("10de", "NVIDIA Corporation"). Разделитель — два пробела.
    private static (string? Id, string? Name) SplitIdName(string line)
    {
        var m = Regex.Match(line, "^([0-9a-fA-F]{4})\\s+(.+)$");
        return m.Success ? (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim()) : (null, null);
    }

    // «GB206 [GeForce RTX 5060 Ti]» → ("GB206", "GeForce RTX 5060 Ti"); без скобок → (name, null).
    private static (string? Chip, string? Model) SplitChipModel(string name)
    {
        var m = Regex.Match(name, "^(.*?)\\s*\\[(.+)\\]\\s*$");
        return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : (name, null);
    }
}
