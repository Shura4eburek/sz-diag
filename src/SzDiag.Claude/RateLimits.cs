using System.Text.Json;

namespace SzDiag.Claude;

/// <param name="Utilization">Доля окна, уже израсходованная (0..1).</param>
public sealed record RateLimitWindow(double Utilization, DateTimeOffset ResetsAt);

/// <summary>Лимиты подписки из `rate_limit_event`: окно 5 часов и недельное.</summary>
public sealed record RateLimitInfo(string Status, RateLimitWindow? FiveHour, RateLimitWindow? SevenDay);

/// <summary>`rate_limit_event` — приходит на каждый ход; в ленту не идёт, только в статусбар.</summary>
public sealed record RateLimitUpdate(RateLimitInfo Info) : ClaudeEvent;

public sealed record LimitsEntry(RateLimitInfo Info, DateTimeOffset SeenAt);

/// <summary>Последние известные лимиты по профилю Claude: лимиты — на аккаунт, у `claude` и
/// `claude2` они свои. Файл `desk-limits.json`: после перезапуска Desk статусбар показывает
/// последнее известное, а не пустоту до первого хода.</summary>
public sealed class LimitsLedger
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Dictionary<string, LimitsEntry> _byProfile;

    public LimitsLedger(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
        _byProfile = Load() ?? new Dictionary<string, LimitsEntry>(StringComparer.Ordinal);
    }

    public event Action? Changed;

    public IReadOnlyDictionary<string, LimitsEntry> All
    {
        get { lock (_gate) return new Dictionary<string, LimitsEntry>(_byProfile, StringComparer.Ordinal); }
    }

    public LimitsEntry? Get(string profile)
    {
        lock (_gate) return _byProfile.GetValueOrDefault(profile);
    }

    public void Update(string profile, RateLimitInfo info)
    {
        lock (_gate)
        {
            _byProfile[profile] = new LimitsEntry(info, _time.GetUtcNow());
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_byProfile)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    private Dictionary<string, LimitsEntry>? Load()
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, LimitsEntry>>(File.ReadAllText(_path));
            return d is null ? null : new Dictionary<string, LimitsEntry>(d, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
