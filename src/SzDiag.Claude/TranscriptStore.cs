using System.Text;

namespace SzDiag.Claude;

/// <summary>Журнал сессии `&lt;dir&gt;\&lt;ключ&gt;.jsonl`: сырые строки stream-json (без служебных) и
/// строки Desk (<see cref="DeskLines"/>). Из него лента восстанавливается после перезапуска Desk.
/// Ошибка записи не роняет сессию — теряется только история.</summary>
public sealed class TranscriptStore(string dir)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();

    public string PathFor(string key)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string(key.Select(c => bad.Contains(c) || c == '.' ? '_' : c).ToArray());
        return Path.Combine(dir, safe + ".jsonl");
    }

    public void Append(string key, string rawLine)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(PathFor(key), rawLine.TrimEnd('\r', '\n') + "\n", Utf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public IReadOnlyList<ClaudeEvent> Load(string key)
    {
        var path = PathFor(key);
        lock (_gate)
        {
            if (!File.Exists(path)) return Array.Empty<ClaudeEvent>();
            try
            {
                return File.ReadLines(path, Utf8)
                    .SelectMany(l => StreamJsonParser.Parse(l, DateTimeOffset.MinValue))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<ClaudeEvent>();
            }
        }
    }
}
