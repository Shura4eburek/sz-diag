using System.Text.Json;

namespace SzDiag.Claude;

/// <param name="SessionId">session_id из первого init; до первого хода — null. При --resume он
/// не меняется (спайк), поэтому хранится один раз.</param>
/// <param name="Profile">Имя профиля Claude (<see cref="ClaudeProfile.Name"/>); null — по умолчанию.
/// Закреплён за сессией: разговор лежит в каталоге профиля, и --resume из другого его не найдёт.</param>
public sealed record SessionRecord(string Key, string? SessionId, DateTimeOffset CreatedAt, bool Archived,
    string? Profile = null);

/// <summary>Реестр `ключ → session_id` (`desk-sessions.json`). Битый файл — пустой реестр, а не
/// падение окна: сессии можно начать заново, а разговоры остаются в профиле claude.</summary>
public sealed class SessionIndex
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionRecord> _items;

    private SessionIndex(string path, Dictionary<string, SessionRecord> items)
    {
        _path = path;
        _items = items;
    }

    public static SessionIndex Load(string path)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<SessionRecord>>(File.ReadAllText(path)) ?? new();
            var items = new Dictionary<string, SessionRecord>(StringComparer.Ordinal);
            foreach (var r in list) items[r.Key] = r;
            return new SessionIndex(path, items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SessionIndex(path, new Dictionary<string, SessionRecord>(StringComparer.Ordinal));
        }
    }

    public IReadOnlyList<SessionRecord> All
    {
        get { lock (_gate) return _items.Values.OrderBy(r => r.Key, StringComparer.Ordinal).ToList(); }
    }

    public SessionRecord? Get(string key)
    {
        lock (_gate) return _items.GetValueOrDefault(key);
    }

    public void Put(SessionRecord r)
    {
        lock (_gate)
        {
            _items[r.Key] = r;
            try
            {
                var full = Path.GetFullPath(_path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var tmp = full + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_items.Values.ToList()));
                File.Move(tmp, full, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
