using System.Text;
using System.Text.RegularExpressions;

namespace SzDiag.Kb;

/// <summary>
/// Лёгкая правка frontmatter (блок между первыми `---`). Скаляры хранятся как сырое
/// значение строки; списки — как токены inline-массива. Тело и неизвестные ключи
/// сохраняются. Формат наш, поэтому без внешнего YAML.
/// </summary>
public sealed class FrontmatterEditor
{
    private sealed class Entry
    {
        public required string Key;
        public string? Scalar;          // сырое значение (для скаляра)
        public List<string>? List;      // токены (для списка)
        public bool IsList => List is not null;
    }

    private readonly List<Entry> _entries;
    private readonly string _body;

    private FrontmatterEditor(List<Entry> entries, string body)
    {
        _entries = entries;
        _body = body;
    }

    public static FrontmatterEditor Load(string content)
    {
        var entries = new List<Entry>();
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n"))
            return new FrontmatterEditor(entries, normalized);

        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return new FrontmatterEditor(entries, normalized);

        var fmBlock = normalized.Substring(4, end - 4);
        var body = normalized.Substring(end + 4).TrimStart('\n');

        foreach (var line in fmBlock.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var key = line.Substring(0, idx).Trim();
            var val = line.Substring(idx + 1).Trim();
            if (val.StartsWith("["))
                entries.Add(new Entry { Key = key, List = ParseList(val) });
            else
                entries.Add(new Entry { Key = key, Scalar = val });
        }
        return new FrontmatterEditor(entries, body);
    }

    private static List<string> ParseList(string bracketed)
    {
        var items = new List<string>();
        foreach (Match m in Regex.Matches(bracketed, "\"(?:[^\"\\\\]|\\\\.)*\""))
            items.Add(m.Value);
        return items;
    }

    public string? GetScalar(string key)
        => _entries.FirstOrDefault(e => e.Key == key && !e.IsList)?.Scalar;

    public IReadOnlyList<string> GetList(string key)
        => _entries.FirstOrDefault(e => e.Key == key && e.IsList)?.List ?? (IReadOnlyList<string>)Array.Empty<string>();

    public void SetScalar(string key, string rawValue)
    {
        var e = _entries.FirstOrDefault(x => x.Key == key);
        if (e is null) { _entries.Add(new Entry { Key = key, Scalar = rawValue }); return; }
        e.Scalar = rawValue;
        e.List = null;
    }

    public void AddToList(string key, string rawItem)
    {
        var e = _entries.FirstOrDefault(x => x.Key == key);
        if (e is null) { _entries.Add(new Entry { Key = key, List = new List<string> { rawItem } }); return; }
        e.List ??= new List<string>();
        e.Scalar = null;
        if (!e.List.Contains(rawItem)) e.List.Add(rawItem);
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        foreach (var e in _entries)
        {
            if (e.IsList)
                sb.Append($"{e.Key}: [{string.Join(", ", e.List!)}]\n");
            else
                sb.Append($"{e.Key}: {e.Scalar}\n");
        }
        sb.Append("---\n\n");
        sb.Append(_body);
        return sb.ToString();
    }
}
