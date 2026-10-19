using System.Text;
using System.Text.RegularExpressions;
using SzDiag.Claude;
using SzDiag.Kb;

namespace SzDiag.Desk.Services;

/// <summary>Соседи для `peers()`/`ask_peer`: живые СЗ из hub с сессией Desk, их железо и похожесть;
/// выжимка — хвосты `діагностика.md` и `журнал.md` из kb. Зовётся с потоков MCP.</summary>
public sealed partial class SzPeerDirectory(KbPaths kb, HwProfileCache hw, SessionManager sessions) : IPeerDirectory
{
    public const int FindingsTailChars = 3000;
    public const int JournalTailLines = 30;

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex SzKey();

    public IReadOnlyList<PeerInfo> Peers(string askerKey)
    {
        var mine = hw.Get(askerKey);
        var withSession = sessions.Records.Where(r => !r.Archived).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        return hw.Live
            .Where(s => s.Sz != askerKey && withSession.Contains(s.Sz))
            .Select(s =>
            {
                var p = hw.Get(s.Sz);
                var similar = mine is not null && p is not null ? HwProfile.Similarity(mine, p) : Array.Empty<string>();
                var state = sessions.Peek(s.Sz)?.State.ToString() ?? "Stopped";
                return new PeerInfo(s.Sz, $"{p?.Line ?? "железо ещё не снято"} · {s.Status} · сессия {state}",
                    similar.Count > 0 ? string.Join(", ", similar) : null);
            })
            .ToList();
    }

    /// <summary>Выжимка по любой СЗ, у которой есть папка в kb, в том числе закрытой: прошлая
    /// похожая заявка — ровно то, что стоит спросить (решение плана части 4). Ключ приходит от
    /// Claude и становится путём на диске — только шесть цифр.</summary>
    public string? Summary(string key)
    {
        if (!SzKey().IsMatch(key)) return null;
        var findings = Read(kb.Findings(key));
        var journal = Read(kb.Journal(key));
        if (string.IsNullOrWhiteSpace(findings) && string.IsNullOrWhiteSpace(journal)) return null;

        var sb = new StringBuilder($"СЗ {key}");
        if (hw.Get(key) is { } p) sb.Append(" · ").Append(p.Line);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(findings))
            sb.AppendLine("== діагностика.md ==")
              .AppendLine(findings.Length <= FindingsTailChars ? findings.Trim() : "…" + findings[^FindingsTailChars..].Trim());
        if (!string.IsNullOrWhiteSpace(journal))
            sb.AppendLine($"== журнал.md (последние {JournalTailLines} строк) ==")
              .AppendLine(string.Join("\n", journal.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(JournalTailLines)));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Журнал в этот момент может дописывать hub — читаем с общим доступом на запись.</summary>
    private static string? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
