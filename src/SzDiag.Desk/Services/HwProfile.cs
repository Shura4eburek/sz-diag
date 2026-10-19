using System.Globalization;
using System.Text.RegularExpressions;

namespace SzDiag.Desk.Services;

/// <summary>Короткий профиль железа для строки на карточке СЗ и для «похожих СЗ». `hw passport`
/// для этого не годится: он снимает только видеокарту (решение плана части 4).</summary>
public sealed record HwProfile(string Cpu, string BoardVendor, string Board, int Modules, int MemoryGb, int MemoryMhz,
    string MemoryParts)
{
    /// <summary>Синхронный exec, без путей и слешей: одна строка на поле, разделитель `|`.</summary>
    public const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $c = Get-CimInstance Win32_Processor | Select-Object -First 1
        $b = Get-CimInstance Win32_BaseBoard | Select-Object -First 1
        $m = @(Get-CimInstance Win32_PhysicalMemory)
        'cpu=' + $c.Name
        'board=' + $b.Manufacturer + '|' + $b.Product
        'mem=' + $m.Count + '|' + [math]::Round(($m | Measure-Object Capacity -Sum).Sum / 1GB) + '|' + ($m | Select-Object -First 1).ConfiguredClockSpeed + '|' + (($m | ForEach-Object { "$($_.PartNumber)".Trim() } | Sort-Object -Unique) -join '/')
        """;

    public static HwProfile? Parse(string stdout)
    {
        var kv = stdout.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains('='))
            .Select(l => (Key: l[..l.IndexOf('=')], Value: l[(l.IndexOf('=') + 1)..].Trim()))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Value);
        if (!kv.ContainsKey("cpu") && !kv.ContainsKey("board")) return null;

        var board = (kv.GetValueOrDefault("board") ?? "").Split('|');
        var mem = (kv.GetValueOrDefault("mem") ?? "").Split('|');
        int N(int i) => i < mem.Length && int.TryParse(mem[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return new HwProfile(kv.GetValueOrDefault("cpu") ?? "", board[0].Trim(), board.Length > 1 ? board[1].Trim() : "",
            N(0), N(1), N(2), mem.Length > 3 ? mem[3].Trim() : "");
    }

    private static readonly Regex CpuNoise = new(
        @"\((R|TM)\)|\bAMD\b|\bIntel\b|\bCore\b(?=(?:\s|\(TM\))+i\d)|\b\d+-Core\b|\bProcessor\b|\bCPU\b|with Radeon.*$|@.*$",
        RegexOptions.IgnoreCase);

    public string CpuShort => Regex.Replace(CpuNoise.Replace(Cpu, " "), @"\s+", " ").Trim();

    public string BoardShort => $"{VendorShort(BoardVendor)} {Board}".Trim();

    public string MemoryText => Modules > 0 && MemoryGb > 0 && MemoryMhz > 0
        ? $"{Modules}×{MemoryGb / Modules} ГБ @ {MemoryMhz}"
        : "";

    public string Line => string.Join(" · ", new[] { CpuShort, BoardShort, MemoryText }.Where(s => s.Length > 0));

    private static string VendorShort(string v) => v switch
    {
        _ when v.StartsWith("ASUSTeK", StringComparison.OrdinalIgnoreCase) => "ASUS",
        _ when v.StartsWith("Micro-Star", StringComparison.OrdinalIgnoreCase) => "MSI",
        _ when v.StartsWith("Gigabyte", StringComparison.OrdinalIgnoreCase) => "Gigabyte",
        _ => v.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "",
    };

    /// <summary>Чем b похож на a. Память — только целым профилем (планки × объём @ частота): одна
    /// частота есть почти в каждой сборке (решение плана части 4). Пустые поля не совпадают.</summary>
    public static IReadOnlyList<string> Similarity(HwProfile a, HwProfile b)
    {
        var r = new List<string>();
        if (Same(a.CpuShort, b.CpuShort)) r.Add($"CPU {a.CpuShort}");
        if (a.Board.Length > 0 && Same(a.BoardShort, b.BoardShort)) r.Add($"плата {a.BoardShort}");
        if (Same(a.MemoryText, b.MemoryText)) r.Add($"память {a.MemoryText}");
        return r;
    }

    private static bool Same(string x, string y) => x.Length > 0 && string.Equals(
        Regex.Replace(x, @"\s+", " ").Trim(), Regex.Replace(y, @"\s+", " ").Trim(), StringComparison.OrdinalIgnoreCase);
}
