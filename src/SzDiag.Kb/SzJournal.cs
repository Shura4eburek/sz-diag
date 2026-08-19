using System.Globalization;
using System.Text;

namespace SzDiag.Kb;

/// <summary>Откуда взялась запись журнала. Значок в файле различает их визуально:
/// команда — без значка, рука мастера — ✋, событие машины — ⚡, дифф снимка железа — 🔧.</summary>
public enum JournalSource { Command, Manual, Machine, Snapshot }

/// <param name="At">Момент события (локальное время сервисного бокса).</param>
public sealed record JournalEntry(DateTimeOffset At, JournalSource Source, string Text);

public interface ISzJournal
{
    void Append(string sz, JournalEntry entry);
    DateTimeOffset? LastEntryAt(string sz);
    IReadOnlyList<JournalEntry> Tail(string sz, int count);
}

/// <summary>Журнал СЗ: единственное место, знающее формат `журнал.md`. Дозапись только
/// в конец файла — журнал не переписывается, чтобы уже записанное нельзя было потерять
/// при сбое посреди операции.</summary>
public sealed class SzJournal : ISzJournal
{
    private const string DayFormat = "yyyy-MM-dd";
    private const string TimeFormat = "HH\\:mm";
    private readonly KbPaths _paths;
    private readonly object _lock = new();

    public SzJournal(KbPaths paths) => _paths = paths;

    public void Append(string sz, JournalEntry entry)
    {
        var path = _paths.Journal(sz);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        lock (_lock)
        {
            var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            var sb = new StringBuilder(existing);

            if (existing.Length == 0)
                sb.Append($"# Журнал {sz}{Environment.NewLine}{Environment.NewLine}");

            var day = entry.At.ToString(DayFormat, CultureInfo.InvariantCulture);
            if (!existing.Contains($"## {day}", StringComparison.Ordinal))
                sb.Append($"## {day}{Environment.NewLine}");

            sb.Append(Line(entry)).Append(Environment.NewLine);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }

    private static string Line(JournalEntry entry)
    {
        var mark = entry.Source switch
        {
            JournalSource.Manual => "✋ ",
            JournalSource.Machine => "⚡ ",
            JournalSource.Snapshot => "🔧 ",
            _ => "",
        };
        var time = entry.At.ToString(TimeFormat, CultureInfo.InvariantCulture);
        return $"- **{time}** {mark}{entry.Text}";
    }

    public DateTimeOffset? LastEntryAt(string sz)
    {
        var entries = ReadAll(sz);
        return entries.Count == 0 ? null : entries[^1].At;
    }

    public IReadOnlyList<JournalEntry> Tail(string sz, int count)
    {
        var entries = ReadAll(sz);
        return count >= entries.Count ? entries : entries[^count..];
    }

    /// <summary>Разбор файла обратно в записи: дата берётся из заголовка дня, время —
    /// из начала строки. Строки, не похожие на запись, пропускаются молча (в файл могли
    /// дописать руками — это не повод падать).</summary>
    private List<JournalEntry> ReadAll(string sz)
    {
        var result = new List<JournalEntry>();
        var path = _paths.Journal(sz);
        if (!File.Exists(path)) return result;

        DateTime? day = null;
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal)
                && DateTime.TryParseExact(line[3..].Trim(), DayFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsedDay))
            {
                day = parsedDay;
                continue;
            }

            if (day is null || !line.StartsWith("- **", StringComparison.Ordinal)) continue;
            var close = line.IndexOf("**", 4, StringComparison.Ordinal);
            if (close < 0) continue;
            if (!TimeSpan.TryParseExact(line[4..close], "hh\\:mm", CultureInfo.InvariantCulture,
                    out var time)) continue;

            var rest = line[(close + 2)..].TrimStart();
            var source = JournalSource.Command;
            foreach (var (mark, kind) in Marks)
            {
                if (!rest.StartsWith(mark, StringComparison.Ordinal)) continue;
                source = kind;
                rest = rest[mark.Length..].TrimStart();
                break;
            }

            result.Add(new JournalEntry(new DateTimeOffset(day.Value + time, TimeSpan.Zero),
                source, rest));
        }

        return result;
    }

    private static readonly (string Mark, JournalSource Kind)[] Marks =
    {
        ("✋", JournalSource.Manual),
        ("⚡", JournalSource.Machine),
        ("🔧", JournalSource.Snapshot),
    };
}
